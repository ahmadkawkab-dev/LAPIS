namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wukna.Features.Auth;
using Wukna.Features.Profile;
using Wukna.Features.Users;
using Xunit;

public sealed class OnboardingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migration_preserves_existing_profiles_and_defaults_the_tour_without_creating_content()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = postgres.CreateContext();
        await db.Database.EnsureDeletedAsync(ct);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261004004046_AddNotificationDelivery", ct);
        var user = new User { Email = "existing-tour@wukna.test", DisplayName = "Existing profile" };
        await PostgresFixture.InsertHistoricalUserAsync(db, user, ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        var saved = await db.Users.SingleAsync(ct);
        Assert.Equal(user.Id, saved.Id);
        Assert.Equal(user.Username, saved.Username);
        Assert.Equal(user.Email, saved.Email);
        Assert.Equal("Existing profile", saved.DisplayName);
        Assert.Equal("NotStarted", saved.OnboardingStatus);
        Assert.Equal(0, saved.OnboardingVersion);
        Assert.Empty(await db.Boards.ToListAsync(ct));
    }

    [Fact]
    public async Task Outcomes_are_authenticated_user_scoped_durable_and_do_not_change_other_preferences()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var first = new User { Email = "tour-first@wukna.test", DisplayName = "First", ProfileImageVersion = "existing-avatar" };
        var second = new User { Email = "tour-second@wukna.test", DisplayName = "Second" };
        await using (var db = postgres.CreateContext()) { db.Users.AddRange(first, second); await db.SaveChangesAsync(ct); }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/profile/onboarding", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/profile/onboarding", new OnboardingDto("Skipped", 1), ct)).StatusCode);
        Authenticate(client, first, clock);
        var initial = await client.GetFromJsonAsync<OnboardingDto>("/api/profile/onboarding", ct);
        Assert.Equal(new OnboardingDto("NotStarted", 0), initial);
        // An injected ownership field has no effect: this endpoint only derives ownership from JWT.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/profile/onboarding",
            new { Status = "Skipped", Version = 1, UserId = second.Id }, ct)).StatusCode);
        Assert.Equal(new OnboardingDto("Skipped", 1), await client.GetFromJsonAsync<OnboardingDto>("/api/profile/onboarding", ct));
        foreach (var _ in Enumerable.Range(0, 2))
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/profile/onboarding", new OnboardingDto("Completed", 1), ct)).StatusCode);
        using var otherBrowser = factory.CreateClient(); Authenticate(otherBrowser, first, clock);
        using var response = await otherBrowser.GetAsync("/api/profile/onboarding", ct);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(new OnboardingDto("Completed", 1), await response.Content.ReadFromJsonAsync<OnboardingDto>(ct));
        Authenticate(otherBrowser, second, clock);
        Assert.Equal(new OnboardingDto("NotStarted", 0), await otherBrowser.GetFromJsonAsync<OnboardingDto>("/api/profile/onboarding", ct));
        await using var check = postgres.CreateContext();
        var saved = await check.Users.SingleAsync(user => user.Id == first.Id, ct);
        Assert.Equal(first.Username, saved.Username);
        Assert.Equal("First", saved.DisplayName);
        Assert.Equal("existing-avatar", saved.ProfileImageVersion);
        Assert.Empty(await check.Boards.ToListAsync(ct));
        Assert.Empty(await check.Notes.ToListAsync(ct));
        Assert.Empty(await check.PlanningSettings.ToListAsync(ct));
        Assert.Empty(await check.NotificationPreferences.ToListAsync(ct));
    }

    [Fact]
    public async Task Invalid_outcomes_and_versions_are_rejected_and_old_clients_cannot_erase_future_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var user = new User { Email = "tour-validation@wukna.test" };
        await using (var db = postgres.CreateContext()) { db.Users.Add(user); await db.SaveChangesAsync(ct); }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = factory.CreateClient(); Authenticate(client, user, clock);
        foreach (var value in new[] { new OnboardingDto("NotStarted", 1), new OnboardingDto("unknown", 1),
            new OnboardingDto("Skipped", 0), new OnboardingDto("Completed", 2), new OnboardingDto(null!, 1) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/profile/onboarding", value, ct)).StatusCode);
        await using (var db = postgres.CreateContext())
            await db.Users.Where(item => item.Id == user.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.OnboardingVersion, 2).SetProperty(item => item.OnboardingStatus, "Completed"), ct);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/profile/onboarding", new OnboardingDto("Skipped", 1), ct)).StatusCode);
        Assert.Equal(new OnboardingDto("Completed", 2), await client.GetFromJsonAsync<OnboardingDto>("/api/profile/onboarding", ct));
    }

    private static void Authenticate(HttpClient client, User user, TimeProvider clock) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtTokenGenerator(new JwtOptions
        {
            Issuer = WuknaWebApplicationFactory.JwtIssuer, Audience = WuknaWebApplicationFactory.JwtAudience,
            SigningKey = WuknaWebApplicationFactory.JwtSigningKey, AccessTokenMinutes = 60, RefreshTokenDays = 7
        }, clock).CreateAccessToken(user).Token);
}
