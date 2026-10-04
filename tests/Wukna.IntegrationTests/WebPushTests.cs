namespace Wukna.IntegrationTests;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Wukna.Features.Notifications;
using Xunit;
using static ChatControlTestSupport;

public sealed class WebPushTests(PostgresFixture postgres)
{
    private static (string Public, string Private) Keys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var values = key.ExportParameters(true);
        return (Base64UrlEncoder.Encode(new byte[] { 4 }.Concat(values.Q.X!).Concat(values.Q.Y!).ToArray()), Base64UrlEncoder.Encode(values.D!));
    }
    private sealed class SenderHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;
        public int Calls { get; private set; }
        public bool Encrypted { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; var body = await request.Content!.ReadAsByteArrayAsync(ct);
            Encrypted = request.Headers.Authorization?.Scheme == "vapid" && request.Content.Headers.ContentEncoding.Contains("aes128gcm") &&
                !System.Text.Encoding.UTF8.GetString(body).Contains("Private title", StringComparison.Ordinal);
            return new HttpResponseMessage(Status);
        }
    }
    private sealed class ClientFactory(SenderHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class PushFactory(PostgresFixture postgres, ManualTimeProvider clock, SenderHandler handler) : WuknaWebApplicationFactory(postgres, clock)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); var keys = Keys();
            builder.UseSetting("WebPush:Enabled", "true"); builder.UseSetting("WebPush:PublicKey", keys.Public);
            builder.UseSetting("WebPush:PrivateKey", keys.Private); builder.UseSetting("WebPush:Subject", "mailto:notifications@wukna.test");
            builder.ConfigureServices(services => { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new ClientFactory(handler)); });
        }
    }
    [Fact]
    public void Operator_key_generation_is_private_valid_and_never_overwrites()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wukna-vapid-test-{Guid.NewGuid():N}.env");
        try
        {
            WebPushConfiguration.WriteKeyPair(path);
            var values = File.ReadAllLines(path).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
            Assert.True(new WebPushOptions { Enabled = true, PublicKey = values["WEB_PUSH_PUBLIC_KEY"],
                PrivateKey = values["WEB_PUSH_PRIVATE_KEY"], Subject = "mailto:test@wukna.test" }.IsValid());
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Throws<IOException>(() => WebPushConfiguration.WriteKeyPair(path));
            Assert.Equal(values["WEB_PUSH_PUBLIC_KEY"], File.ReadAllLines(path)[0].Split('=', 2)[1]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task Registration_binds_authenticated_accounts_validates_provider_keys_and_caps_devices()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct); var handler = new SenderHandler();
        await using var factory = new PushFactory(postgres, seed.Clock, handler); using var guest = Client(factory, seed.Guest); using var other = Client(factory, seed.Outsider);
        var keys = Keys(); var installation = Guid.NewGuid(); var endpoint = "https://fcm.googleapis.com/fcm/send/test-endpoint";
        var registration = new PushRegistration(installation, endpoint, keys.Public, Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)));
        foreach (var invalid in new[] { "http://fcm.googleapis.com/test", "https://127.0.0.1/", "https://fcm.googleapis.com.evil.test/", "https://fcm.googleapis.com:8443/", "https://user@fcm.googleapis.com/", "https://169.254.169.254/" })
            Assert.Equal(HttpStatusCode.BadRequest, (await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", registration with { Endpoint = invalid }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", registration with { Auth = "bad" }, ct)).StatusCode);
        using var saved = await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", new { installationId = installation, endpoint, p256dh = keys.Public, auth = registration.Auth, userId = seed.Outsider.Id }, ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", registration, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PutAsJsonAsync("/api/notifications/push/subscriptions", registration, ct)).StatusCode);
        var devices = (await guest.GetFromJsonAsync<System.Text.Json.JsonElement[]>("/api/notifications/push/subscriptions", ct))!; var id = Assert.Single(devices).GetProperty("id").GetGuid();
        Assert.False(devices[0].TryGetProperty("endpoint", out _));
        Assert.Empty((await other.GetFromJsonAsync<System.Text.Json.JsonElement[]>("/api/notifications/push/subscriptions", ct))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/notifications/push/subscriptions/{id}", ct)).StatusCode);
        for (var index = 1; index < 8; index++) Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", registration with { InstallationId = Guid.NewGuid(), Endpoint = endpoint + index }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", registration with { InstallationId = Guid.NewGuid(), Endpoint = endpoint + "overflow" }, ct)).StatusCode);
        await using var db = postgres.CreateContext(); var item = await db.BrowserPushSubscriptions.SingleAsync(item => item.Id == id, ct);
        Assert.Equal(seed.Guest.Id, item.UserId); Assert.DoesNotContain(endpoint, item.EndpointProtected);
        Assert.Equal(endpoint, factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Wukna.WebPush.Subscriptions.v1").Unprotect(item.EndpointProtected));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task Push_fanout_is_idempotent_foreground_lease_suppresses_it_and_gone_devices_are_disabled()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct); var handler = new SenderHandler();
        await using var factory = new PushFactory(postgres, seed.Clock, handler); using var guest = Client(factory, seed.Guest);
        var keys = Keys(); var installation = Guid.NewGuid(); var registration = new PushRegistration(installation,
            "https://fcm.googleapis.com/fcm/send/test", keys.Public, Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)));
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", registration, ct)).StatusCode);
        await using var db = postgres.CreateContext();
        db.NotificationPreferences.Add(new() { UserId = seed.Guest.Id, PushEnabled = true, UpdatedAt = seed.Clock.GetUtcNow() });
        var item = new Notification { UserId = seed.Guest.Id, Type = NotificationType.SharedBoardActivity, Title = "Private title",
            IssuedAt = seed.Clock.GetUtcNow(), UpdatedAt = seed.Clock.GetUtcNow() }; db.Notifications.Add(item); await db.SaveChangesAsync(ct);
        var sender = factory.Services.GetRequiredService<INotificationPushSender>();
        var tab = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PutAsJsonAsync("/api/notifications/push/presence", new ClientPresenceRequest(installation, tab, true, null, false), ct)).StatusCode);
        await sender.QueueAsync(db, item, seed.Clock.GetUtcNow(), ct); Assert.Empty(await db.NotificationWork.Where(work => work.Kind == NotificationWorkKind.Push).ToArrayAsync(ct));
        seed.Clock.Advance(TimeSpan.FromSeconds(31));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ => { await using var context = postgres.CreateContext(); await sender.QueueAsync(context, item, seed.Clock.GetUtcNow(), ct); }));
        var work = Assert.Single(await db.NotificationWork.Where(work => work.Kind == NotificationWorkKind.Push).ToArrayAsync(ct));
        var preference = await db.NotificationPreferences.SingleAsync(ct);
        await sender.SendAsync(db, work, item, preference, ct); Assert.Equal(1, handler.Calls); Assert.True(handler.Encrypted);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(db, work, item, preference, ct));
        Assert.Null((await db.BrowserPushSubscriptions.SingleAsync(ct)).DisabledAt);
        handler.Status = HttpStatusCode.Gone; await sender.SendAsync(db, work, item, preference, ct);
        Assert.NotNull((await db.BrowserPushSubscriptions.SingleAsync(ct)).DisabledAt);
        await sender.SendAsync(db, work, item, preference, ct); Assert.Equal(3, handler.Calls);
    }
    [Fact]
    public async Task Durable_push_failure_retries_then_rechecks_preferences_before_delivery()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        var handler = new SenderHandler { Status = HttpStatusCode.ServiceUnavailable };
        await using var factory = new PushFactory(postgres, seed.Clock, handler); using var guest = Client(factory, seed.Guest);
        var keys = Keys(); var registration = new PushRegistration(Guid.NewGuid(), "https://fcm.googleapis.com/fcm/send/retry",
            keys.Public, Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)));
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync("/api/notifications/push/subscriptions", registration, ct)).StatusCode);
        await using (var db = postgres.CreateContext())
        {
            db.NotificationPreferences.Add(new() { UserId = seed.Guest.Id, PushEnabled = true, UpdatedAt = seed.Clock.GetUtcNow() });
            var item = new Notification { UserId = seed.Guest.Id, Type = NotificationType.SharedBoardActivity, Title = "Private title",
                IssuedAt = seed.Clock.GetUtcNow(), UpdatedAt = seed.Clock.GetUtcNow() };
            db.Notifications.Add(item); NotificationSources.Deliver(db, item, seed.Clock.GetUtcNow()); await db.SaveChangesAsync(ct);
        }
        var dispatcher = factory.Services.GetRequiredService<NotificationDispatcher>();
        await dispatcher.ProcessAsync(ct); // SignalR work commits independent push work.
        await dispatcher.ProcessAsync(ct); // Provider failure leaves a future retry with no active lease.
        Assert.Equal(1, handler.Calls);
        await using (var db = postgres.CreateContext())
        {
            var work = await db.NotificationWork.SingleAsync(item => item.Kind == NotificationWorkKind.Push, ct);
            Assert.Null(work.ProcessedAt); Assert.Null(work.LeaseToken); Assert.Null(work.LeaseUntil);
            Assert.Equal(1, work.Attempts); Assert.True(work.NextAttemptAt > seed.Clock.GetUtcNow());
            await db.NotificationPreferences.Where(item => item.UserId == seed.Guest.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.PushEnabled, false), ct);
        }
        seed.Clock.Advance(TimeSpan.FromSeconds(5)); handler.Status = HttpStatusCode.Created;
        await dispatcher.ProcessAsync(ct); Assert.Equal(1, handler.Calls);
        await using var final = postgres.CreateContext();
        Assert.NotNull((await final.NotificationWork.SingleAsync(item => item.Kind == NotificationWorkKind.Push, ct)).ProcessedAt);
        Assert.Single(await final.Notifications.ToArrayAsync(ct));
    }

}
