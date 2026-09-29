namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Wukna.Features.Auth.DTOs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class SessionRestorationTests(PostgresFixture postgres)
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_or_stale_CSRF_does_not_consume_the_refresh_cookie_and_fresh_CSRF_restores_profile_access()
    {
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory);
        var registered = await Register(client);
        foreach (var token in new string?[] { null, "stale" })
        {
            using var invalid = await Refresh(client, registered.RefreshCookie, registered.Csrf.Cookie, token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid_csrf", (await invalid.Content.ReadFromJsonAsync<CsrfError>(cancellationToken))?.Code);
        }
        using var missingCookie = await Refresh(client, registered.RefreshCookie, "", registered.Csrf.Token);
        Assert.Equal(HttpStatusCode.BadRequest, missingCookie.StatusCode);
        await using (var db = postgres.CreateContext())
            Assert.Equal(1, await db.RefreshTokens.CountAsync(t => !t.IsRevoked, cancellationToken));
        var fresh = await Csrf(client); // Browser reopened without its session-only antiforgery cookie.
        using var refreshed = await Refresh(client, registered.RefreshCookie, fresh.Cookie, fresh.Token);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var restored = Assert.IsType<AuthResponseDto>(await refreshed.Content.ReadFromJsonAsync<AuthResponseDto>(cancellationToken));
        Assert.Equal(registered.Session.User.Id, restored.User.Id);
        Assert.NotEqual(registered.RefreshCookie, Cookie(refreshed, "wukna.refresh"));
        using var profile = await Profile(client, restored.AccessToken);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var replay = await Refresh(client, registered.RefreshCookie, fresh.Cookie, fresh.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Bearer_bound_CSRF_does_not_replace_anonymous_refresh_credentials()
    {
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory);
        var registered = await Register(client);
        var bound = await Csrf(client, registered.Csrf.Cookie, registered.Session.AccessToken);
        using var wrongIdentity = await Refresh(client, registered.RefreshCookie, bound.Cookie, bound.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongIdentity.StatusCode);
        var anonymous = await Csrf(client, bound.Cookie);
        using var restored = await Refresh(client, registered.RefreshCookie, anonymous.Cookie, anonymous.Token);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("invalid")]
    public async Task Invalid_refresh_sessions_require_login_after_successful_CSRF_acquisition(string reason)
    {
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory);
        var registered = await Register(client);
        if (reason == "expired") clock.Advance(TimeSpan.FromDays(8));
        if (reason == "revoked")
        {
            await using var db = postgres.CreateContext();
            await db.RefreshTokens.ExecuteUpdateAsync(setters => setters.SetProperty(t => t.IsRevoked, true), cancellationToken);
        }
        var fresh = await Csrf(client);
        var refreshCookie = reason == "missing" ? "" : reason == "invalid" ? "wukna.refresh=invalid" : registered.RefreshCookie;
        using var response = await Refresh(client, refreshCookie, fresh.Cookie, fresh.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("expires=", response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("wukna.refresh=")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replacing_the_application_preserves_antiforgery_and_refresh_sessions_with_the_same_key_volume()
    {
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var directory = Path.Combine(Path.GetTempPath(), "wukna-key-restart-" + Guid.NewGuid().ToString("N"));
        Registration registered;
        string protectedValue;
        try
        {
            await using (var first = new KeyRingFactory(postgres, clock, directory))
            {
                using var client = Client(first);
                registered = await Register(client);
                protectedValue = first.Services.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("restart-regression").Protect("persisted-auth-value");
                Assert.NotEmpty(Directory.EnumerateFiles(directory, "key-*.xml"));
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
                Assert.Contains("secure", registered.RefreshCookieHeader, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("httponly", registered.RefreshCookieHeader, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("samesite=strict", registered.RefreshCookieHeader, StringComparison.OrdinalIgnoreCase);
            }
            await using (var replacement = new KeyRingFactory(postgres, clock, directory))
            {
                using var client = Client(replacement);
                Assert.Equal("persisted-auth-value", replacement.Services.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("restart-regression").Unprotect(protectedValue));
                // Previously issued cookie/token pair survives a new host and key provider.
                using var response = await Refresh(client, registered.RefreshCookie, registered.Csrf.Cookie, registered.Csrf.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var restored = Assert.IsType<AuthResponseDto>(await response.Content.ReadFromJsonAsync<AuthResponseDto>(cancellationToken));
                Assert.Equal(registered.Session.User.Id, restored.User.Id);
                using var profile = await Profile(client, restored.AccessToken);
                Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
                var fresh = await Csrf(client, registered.Csrf.Cookie);
                using var next = await Refresh(client, Cookie(response, "wukna.refresh"), fresh.Cookie, fresh.Token);
                Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Fresh_CSRF_can_recover_from_an_unreadable_old_antiforgery_cookie_without_invalidating_the_refresh_token()
    {
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var root = Path.Combine(Path.GetTempPath(), "wukna-key-loss-control-" + Guid.NewGuid().ToString("N"));
        try
        {
            Registration registered;
            await using (var first = new KeyRingFactory(postgres, clock, Path.Combine(root, "old")))
            {
                using var client = Client(first); registered = await Register(client);
            }
            await using (var replacement = new KeyRingFactory(postgres, clock, Path.Combine(root, "different")))
            {
                using var client = Client(replacement);
                using var rejected = await Refresh(client, registered.RefreshCookie, registered.Csrf.Cookie, registered.Csrf.Token);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                var fresh = await Csrf(client, registered.Csrf.Cookie);
                Assert.NotEqual(registered.Csrf.Cookie, fresh.Cookie);
                using var restored = await Refresh(client, registered.RefreshCookie, fresh.Cookie, fresh.Token);
                Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private async Task<Registration> Register(HttpClient client)
    {
        var csrf = await Csrf(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new RegisterRequestDto("restore@wukna.test", "ValidPassword123!"))
        };
        request.Headers.Add("Cookie", csrf.Cookie); request.Headers.Add("X-CSRF-TOKEN", csrf.Token);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new Registration(Assert.IsType<AuthResponseDto>(await response.Content.ReadFromJsonAsync<AuthResponseDto>(cancellationToken)),
            Cookie(response, "wukna.refresh"), response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("wukna.refresh=")), csrf);
    }
    private async Task<CsrfCredentials> Csrf(HttpClient client, string? existing = null, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        if (!string.IsNullOrEmpty(existing)) request.Headers.Add("Cookie", existing);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode(); Assert.True(response.Headers.CacheControl?.NoStore);
        var token = Assert.IsType<CsrfResponse>(await response.Content.ReadFromJsonAsync<CsrfResponse>(cancellationToken));
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(c => c.StartsWith("wukna.csrf="))?.Split(';')[0] : null;
        return new CsrfCredentials(token.Token, cookie ?? existing ?? throw new InvalidOperationException("CSRF cookie missing"));
    }
    private async Task<HttpResponseMessage> Refresh(HttpClient client, string refreshCookie, string csrfCookie, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        var cookies = string.Join("; ", new[] { refreshCookie, csrfCookie }.Where(c => !string.IsNullOrEmpty(c)));
        if (cookies.Length > 0) request.Headers.Add("Cookie", cookies);
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request, cancellationToken);
    }
    private async Task<HttpResponseMessage> Profile(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
    });
    private static string Cookie(HttpResponseMessage response, string name) => response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(name + "=")).Split(';')[0];
    private sealed record Registration(AuthResponseDto Session, string RefreshCookie, string RefreshCookieHeader, CsrfCredentials Csrf);
    private sealed record CsrfCredentials(string Token, string Cookie);
    private sealed record CsrfResponse(string Token);
    private sealed record CsrfError(string Code);
    private sealed class KeyRingFactory(PostgresFixture postgres, ManualTimeProvider clock, string path) : WuknaWebApplicationFactory(postgres, clock)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.UseSetting("DataProtection:KeysPath", path);
            builder.UseSetting("Authentication:Google:FrontendBaseUrl", "https://wukna.test");
            builder.UseSetting("ReverseProxy:KnownProxyIp", "127.0.0.1");
        }
    }
}
