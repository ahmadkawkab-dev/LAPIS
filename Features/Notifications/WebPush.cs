namespace Wukna.Features.Notifications;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Wukna.Shared.Data.AppDbContext;

public sealed class WebPushOptions
{
    public bool Enabled { get; set; }
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool IsValid()
    {
        if (!Enabled) return true;
        try
        {
            var point = Base64UrlEncoder.DecodeBytes(PublicKey); var secret = Base64UrlEncoder.DecodeBytes(PrivateKey);
            if (point.Length != 65 || point[0] != 4 || secret.Length != 32 ||
                !(Subject.StartsWith("mailto:", StringComparison.Ordinal) && Subject.Length > 7 ||
                  Uri.TryCreate(Subject, UriKind.Absolute, out var uri) && uri.Scheme == "https")) return false;
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = secret });
            var exported = key.ExportParameters(false);
            return CryptographicOperations.FixedTimeEquals(point.AsSpan(1, 32), exported.Q.X) &&
                CryptographicOperations.FixedTimeEquals(point.AsSpan(33, 32), exported.Q.Y);
        }
        catch { return false; }
    }
}
public sealed class BrowserPushSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid InstallationId { get; set; }
    public string EndpointHash { get; set; } = "";
    public string EndpointProtected { get; set; } = "";
    public string P256dhProtected { get; set; } = "";
    public string AuthProtected { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public int FailureCount { get; set; }
}
public sealed class BrowserPushSubscriptionConfiguration : IEntityTypeConfiguration<BrowserPushSubscription>
{
    public void Configure(EntityTypeBuilder<BrowserPushSubscription> entity)
    {
        entity.ToTable("browser_push_subscriptions"); entity.HasKey(item => item.Id);
        entity.Property(item => item.EndpointHash).HasMaxLength(64);
        entity.HasIndex(item => item.EndpointHash).IsUnique();
        entity.HasIndex(item => new { item.UserId, item.InstallationId }).IsUnique();
        entity.HasOne<Wukna.Features.Users.User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed record PushRegistration(Guid InstallationId, string Endpoint, string P256dh, string Auth);
public sealed record ClientPresenceRequest(Guid InstallationId, Guid TabId, bool Visible, Guid? BoardId, bool ChatVisible);

public static class WebPushEndpoints
{
    public static IEndpointRouteBuilder MapWebPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/notifications/push").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        group.MapGet("/configuration", (IOptions<WebPushOptions> options) => Results.Ok(new {
            enabled = options.Value.Enabled, publicKey = options.Value.Enabled ? options.Value.PublicKey : null }));
        group.MapGet("/subscriptions", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            return Results.Ok(await db.BrowserPushSubscriptions.Where(item => item.UserId == userId && item.DisabledAt == null)
                .Select(item => new { item.Id, item.InstallationId, item.CreatedAt }).ToArrayAsync(ct));
        });
        group.MapPut("/subscriptions", async (PushRegistration request, HttpContext context, WuknaDbContext db,
            IDataProtectionProvider protection, IOptions<WebPushOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!options.Value.Enabled) return Results.Conflict(new { code = "push_unavailable" });
            if (request.InstallationId == Guid.Empty || !WebPushSender.ValidEndpoint(request.Endpoint) || !ValidKeys(request.P256dh, request.Auth))
                return Results.BadRequest(new { code = "invalid_push_subscription" });
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Endpoint)));
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Serialize registration and per-account limits without accepting any client owner field.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM asp_net_users WHERE id = {userId} FOR UPDATE", ct);
            var existing = await db.BrowserPushSubscriptions.SingleOrDefaultAsync(item => item.EndpointHash == hash, ct);
            if (existing is not null && (existing.UserId != userId || existing.InstallationId != request.InstallationId))
                return Results.Conflict(new { code = "push_subscription_bound_elsewhere" });
            var item = await db.BrowserPushSubscriptions.SingleOrDefaultAsync(item => item.UserId == userId && item.InstallationId == request.InstallationId, ct);
            if (item is null && await db.BrowserPushSubscriptions.CountAsync(item => item.UserId == userId && item.DisabledAt == null, ct) >= 8)
                return Results.Conflict(new { code = "push_device_limit" });
            var now = clock.GetUtcNow();
            if (item is null) { item = new() { UserId = userId, InstallationId = request.InstallationId, CreatedAt = now }; db.BrowserPushSubscriptions.Add(item); }
            var protector = protection.CreateProtector("Wukna.WebPush.Subscriptions.v1");
            item.EndpointHash = hash; item.EndpointProtected = protector.Protect(request.Endpoint);
            item.P256dhProtected = protector.Protect(request.P256dh); item.AuthProtected = protector.Protect(request.Auth);
            item.UpdatedAt = now; item.DisabledAt = null; item.FailureCount = 0;
            try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            { return Results.Conflict(new { code = "push_subscription_bound_elsewhere" }); }
            return Results.Ok(new { item.Id, item.InstallationId });
        });
        group.MapDelete("/subscriptions/{id:guid}", async (Guid id, HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var updated = await db.BrowserPushSubscriptions.Where(item => item.Id == id && item.UserId == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.DisabledAt, clock.GetUtcNow()), ct);
            return updated == 0 ? Results.NotFound() : Results.NoContent();
        });
        group.MapDelete("/installation/{id:guid}", async (Guid id, HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.BrowserPushSubscriptions.Where(item => item.InstallationId == id && item.UserId == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.DisabledAt, clock.GetUtcNow()), ct);
            await db.NotificationClientPresence.Where(item => item.InstallationId == id && item.UserId == userId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct); return Results.NoContent();
        });
        group.MapPut("/presence", async (ClientPresenceRequest request, HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (request.InstallationId == Guid.Empty || request.TabId == Guid.Empty) return Results.BadRequest();
            if (request.BoardId is { } boardId && !await db.BoardMemberships.AnyAsync(item => item.UserId == userId && item.BoardId == boardId, ct)) return Results.NotFound();
            var expires = request.Visible ? clock.GetUtcNow().AddSeconds(30) : clock.GetUtcNow();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_client_presence (user_id, installation_id, tab_id, board_id, chat_visible, expires_at)
                VALUES ({userId}, {request.InstallationId}, {request.TabId}, {request.BoardId}, {request.Visible && request.ChatVisible}, {expires})
                ON CONFLICT (user_id, installation_id, tab_id) DO UPDATE SET board_id = EXCLUDED.board_id,
                  chat_visible = EXCLUDED.chat_visible, expires_at = EXCLUDED.expires_at
                """, ct); return Results.NoContent();
        }).RequireRateLimiting("chat-read");
        return endpoints;
    }
    private static bool ValidKeys(string p256dh, string auth)
    {
        if (p256dh is null || auth is null || p256dh.Length > 100 || auth.Length > 30) return false;
        try {
            var point = Base64UrlEncoder.DecodeBytes(p256dh); var secret = Base64UrlEncoder.DecodeBytes(auth);
            if (point.Length != 65 || point[0] != 4 || secret.Length != 16) return false;
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33], Y = point[33..65] } });
            return true;
        } catch { return false; }
    }
}

public sealed class WebPushSender(IOptions<WebPushOptions> options, IDataProtectionProvider protection,
    IHttpClientFactory clients, TimeProvider clock) : INotificationPushSender
{
    public static bool ValidEndpoint(string endpoint) => endpoint is { Length: > 0 and <= 2048 } &&
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (uri.Host == "fcm.googleapis.com" || uri.Host == "web.push.apple.com" ||
         uri.Host == "updates.push.services.mozilla.com" || uri.Host.EndsWith(".push.services.mozilla.com", StringComparison.Ordinal));

    public async Task QueueAsync(WuknaDbContext db, Notification notification, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.Value.Enabled || notification.UpdatedAt < now.AddMinutes(-5) || notification.ReadRevision >= notification.Revision) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_work (id, kind, user_id, source_event_key, type, notification_id, notification_revision,
                push_subscription_id, created_at, next_attempt_at, expires_at, attempts)
            SELECT gen_random_uuid(), 2, subscription.user_id,
                'push:' || {notification.Id.ToString("N")} || ':' || {notification.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)} || ':' || subscription.id,
                {(int)notification.Type}, {notification.Id}, {notification.Revision}, subscription.id, {now}, {now}, {now.AddMinutes(5)}, 0
            FROM browser_push_subscriptions AS subscription
            JOIN notification_preferences AS preference ON preference.user_id = subscription.user_id
            WHERE subscription.user_id = {notification.UserId} AND subscription.disabled_at IS NULL AND preference.push_enabled
              AND NOT EXISTS (SELECT 1 FROM notification_client_presence AS presence WHERE presence.user_id = subscription.user_id
                AND presence.installation_id = subscription.installation_id AND presence.expires_at > {now})
            ON CONFLICT (user_id, source_event_key, kind) DO NOTHING
            """, ct);
    }
    public async Task SendAsync(WuknaDbContext db, NotificationWork work, Notification notification, NotificationPreference preference, CancellationToken ct)
    {
        if (!options.Value.Enabled || work.PushSubscriptionId is null) return;
        preference = await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(pref => pref.UserId == work.UserId, ct) ?? new NotificationPreference();
        if (!preference.PushEnabled || !NotificationDispatcher.Enabled(preference, notification.Type) ||
            !await NotificationEndpoints.Visible(db, work.UserId).AnyAsync(current => current.Id == notification.Id &&
                current.Revision == notification.Revision && current.DismissedAt == null && current.ReadRevision < current.Revision, ct)) return;
        var item = await db.BrowserPushSubscriptions.SingleOrDefaultAsync(item => item.Id == work.PushSubscriptionId && item.UserId == work.UserId && item.DisabledAt == null, ct);
        if (item is null || await db.NotificationClientPresence.AnyAsync(presence => presence.UserId == work.UserId &&
            presence.InstallationId == item.InstallationId && presence.ExpiresAt > clock.GetUtcNow(), ct)) return;
        var protector = protection.CreateProtector("Wukna.WebPush.Subscriptions.v1");
        var endpoint = protector.Unprotect(item.EndpointProtected);
        if (!ValidEndpoint(endpoint)) { item.DisabledAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct); return; }
        var subscription = new PushSubscription { Endpoint = endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, protector.Unprotect(item.P256dhProtected));
        subscription.SetKey(PushEncryptionKeyName.Auth, protector.Unprotect(item.AuthProtected));
        var muted = preference.SoundsMuted || preference.SoundVolume <= 0 || !SoundEnabled(preference, notification);
        if (notification.BoardId is { } boardId)
            muted |= await db.BoardNotificationPreferences.AnyAsync(pref => pref.UserId == work.UserId && pref.BoardId == boardId && pref.SoundsMuted, ct);
        // The default payload contains no board title, task title, message body, email, or credential.
        var payload = JsonSerializer.Serialize(new { userId = work.UserId, installationId = item.InstallationId,
            notificationId = notification.Id, revision = notification.Revision, title = "Wukna",
            body = preference.PrivatePreviewsEnabled ? notification.Title : "You have new activity", silent = muted,
            path = NotificationPath(notification), tag = $"wukna:{notification.Id:N}", issuedAt = notification.UpdatedAt });
        using var http = clients.CreateClient("web-push");
        var client = new PushServiceClient(http) { AutoRetryAfter = false };
        using var authentication = new VapidAuthentication(options.Value.PublicKey, options.Value.PrivateKey) { Subject = options.Value.Subject };
        try
        {
            await client.RequestPushMessageDeliveryAsync(subscription, new PushMessage(payload) { TimeToLive = 300,
                Topic = notification.Id.ToString("N"), Urgency = PushMessageUrgency.Normal }, authentication, ct);
            item.FailureCount = 0; item.UpdatedAt = clock.GetUtcNow(); await db.SaveChangesAsync(ct);
        }
        catch (PushServiceClientException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        { item.DisabledAt = clock.GetUtcNow(); item.FailureCount++; await db.SaveChangesAsync(ct); }
        catch (PushServiceClientException error) when ((int)error.StatusCode is >= 400 and < 500 && error.StatusCode != HttpStatusCode.TooManyRequests)
        { item.DisabledAt = clock.GetUtcNow(); item.FailureCount++; await db.SaveChangesAsync(ct); }
        catch (PushServiceClientException)
        { item.FailureCount++; await db.SaveChangesAsync(ct); throw new HttpRequestException("Push provider temporarily unavailable."); }
    }
    public static bool SoundEnabled(NotificationPreference preference, Notification item) => item.Type switch {
        NotificationType.ChatActivity => item.ActivityKind == "scheduledTaskPosted" ? preference.ScheduledTaskPostedSoundEnabled : preference.ChatSoundEnabled,
        NotificationType.TaskReminder or NotificationType.ScheduledTaskReminder => preference.TaskReminderSoundEnabled,
        NotificationType.BoardInvitation => preference.BoardInvitationSoundEnabled,
        NotificationType.TaskActivity => item.ActivityKind == "taskCompleted" && preference.TaskCompletedSoundEnabled,
        _ => false };
    public static string NotificationPath(Notification item) => item.Type == NotificationType.TaskReminder ? $"/tasks/{item.TaskId}" :
        item.ResourceKind == "calendarEvent" ? $"/calendar?event={item.ResourceId}" :
        item.BoardId is { } boardId ? item.ResourceKind == "chatMessage" ? $"/boards/{boardId}?chat=1&message={item.ResourceId}" :
            item.ResourceKind == "note" ? $"/boards/{boardId}?note={item.ResourceId}" : $"/boards/{boardId}" : "/notifications";

    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseProxy = false,
        ConnectCallback = async (context, ct) => {
            if (!ValidEndpoint($"https://{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}/")) throw new HttpRequestException("Unsupported push provider.");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var address = addresses.FirstOrDefault(IsPublicAddress) ?? throw new HttpRequestException("Push provider has no public address.");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        } };
    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) && !(bytes[0] == 192 && bytes[1] == 168) &&
                !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20;
    }
}
