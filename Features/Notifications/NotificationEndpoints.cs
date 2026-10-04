namespace Wukna.Features.Notifications;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed record NotificationDto(Guid Id, string Type, string Title, string? ActivityKind,
    Guid? ActorUserId, Guid? BoardId, string? ResourceKind, Guid? ResourceId, DateTimeOffset IssuedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? ReadAt, DateTimeOffset? DismissedAt,
    long Revision, long ReadRevision, bool IsUnread, int ActivityCount,
    Guid? TaskId, string? TaskTitle)
{
    public static NotificationDto From(Notification item) => new(item.Id, item.Type switch
    {
        NotificationType.TaskReminder => "taskReminder",
        NotificationType.ChatActivity => "chatActivity",
        NotificationType.ScheduledTaskReminder => "scheduledTaskReminder",
        NotificationType.SharedBoardActivity => "sharedBoardActivity",
        NotificationType.BoardInvitation => "boardInvitation",
        NotificationType.TaskActivity => "taskActivity",
        _ => throw new InvalidOperationException("Unknown notification type.")
    }, item.Task?.Title ?? item.Title, item.ActivityKind, item.ActorUserId, item.BoardId,
        item.ResourceKind, item.ResourceId, item.IssuedAt, item.UpdatedAt, item.ReadAt, item.DismissedAt,
        item.Revision, item.ReadRevision, item.ReadRevision < item.Revision, item.ActivityCount,
        item.TaskId, item.TaskId is null ? null : item.Task?.Title ?? item.Title);
}

public sealed record NotificationPageDto(IReadOnlyList<NotificationDto> Items, string? NextCursor,
    int TotalCount, int UnreadCount);
public sealed record NotificationUnreadCountDto(int UnreadCount);
public sealed record DismissReadNotificationsDto(int DismissedCount);

public static class NotificationEndpoints
{
    // The membership incarnation prevents old notifications reappearing after re-invitation.
    public static IQueryable<Notification> Visible(WuknaDbContext db, Guid userId) =>
        db.Notifications.Where(item => item.UserId == userId &&
            (item.BoardId == null || db.BoardMemberChatStates.Any(state =>
                state.BoardId == item.BoardId && state.UserId == userId &&
                state.MembershipInstanceId == item.MembershipInstanceId)));

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/notifications").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        group.MapGet("/{id:guid}", async (Guid id, HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var item = await Visible(db, userId).AsNoTracking().Include(item => item.Task).SingleOrDefaultAsync(item => item.Id == id && item.DismissedAt == null, ct);
            return item is null ? Results.NotFound() : Results.Ok(NotificationDto.From(item));
        });
        group.MapGet("/chat-unread", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var rows = await db.BoardMemberships.Where(member => member.UserId == userId).Select(member => new {
                member.BoardId, unreadCount = db.ChatMessages.Count(message => message.BoardId == member.BoardId && message.SenderUserId != userId &&
                  message.Sequence > (db.BoardMemberChatStates.Where(state => state.BoardId == member.BoardId && state.UserId == userId).Select(state => (long?)state.LastReadSequence).FirstOrDefault() ?? 0))
            }).ToArrayAsync(ct);
            return Results.Ok(rows);
        });
        group.MapGet("/page", async (int? limit, string? cursor, bool? unreadOnly,
            HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (limit is < 1 or > 100 || !TryParseCursor(cursor, out var time, out var id))
                return BadRequest("invalid_notification_page");
            var active = Visible(db, userId).AsNoTracking().Where(item => item.DismissedAt == null);
            var total = await active.CountAsync(ct);
            var unread = await active.CountAsync(item => item.ReadRevision < item.Revision, ct);
            var query = unreadOnly == true ? active.Where(item => item.ReadRevision < item.Revision) : active;
            if (cursor is not null) query = query.Where(item => item.IssuedAt < time ||
                item.IssuedAt == time && item.Id.CompareTo(id) < 0);
            var size = limit ?? 30;
            var rows = await query.Include(item => item.Task).OrderByDescending(item => item.IssuedAt)
                .ThenByDescending(item => item.Id).Take(size + 1).ToArrayAsync(ct);
            var items = rows.Take(size).Select(NotificationDto.From).ToArray();
            var next = rows.Length > size ? MakeCursor(items[^1].IssuedAt, items[^1].Id) : null;
            return Results.Ok(new NotificationPageDto(items, next, total, unread));
        });
        group.MapGet("/", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var rows = await Visible(db, userId).AsNoTracking().Where(item => item.DismissedAt == null)
                .Include(item => item.Task).OrderByDescending(item => item.IssuedAt)
                .ThenByDescending(item => item.Id).Take(100).ToArrayAsync(ct);
            return Results.Ok(rows.Select(NotificationDto.From));
        });
        group.MapGet("/unread-count", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var count = await Visible(db, userId).CountAsync(item => item.DismissedAt == null &&
                item.ReadRevision < item.Revision, ct);
            return Results.Ok(new NotificationUnreadCountDto(count));
        });
        group.MapPost("/read-all", async (HttpContext context, WuknaDbContext db, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await Visible(db, userId).Where(item => item.DismissedAt == null && item.ReadRevision < item.Revision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReadAt, clock.GetUtcNow())
                    .SetProperty(item => item.ReadRevision, item => item.Revision), ct);
            NotificationSources.StateChanged(db, userId, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
        group.MapPost("/dismiss-read", async (HttpContext context, WuknaDbContext db, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            // Compare current revisions in the UPDATE, so new unread activity is never dismissed.
            var dismissed = await Visible(db, userId)
                .Where(item => item.DismissedAt == null && item.ReadRevision >= item.Revision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DismissedAt, now), ct);
            if (dismissed > 0)
            {
                NotificationSources.StateChanged(db, userId, now);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new DismissReadNotificationsDto(dismissed));
        });
        group.MapPost("/{id:guid}/read", async (Guid id, long? revision, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (revision is < 1) return BadRequest("invalid_notification_revision");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var query = Visible(db, userId).Where(item => item.Id == id);
            // Legacy task-reminder callers may omit revision; new activity requires an observed revision.
            if (revision is null) query = query.Where(item => item.Type == NotificationType.TaskReminder);
            else query = query.Where(item => item.Revision >= revision.Value);
            var updated = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ReadAt, clock.GetUtcNow())
                .SetProperty(item => item.ReadRevision, item => revision == null ? item.Revision :
                    item.ReadRevision > revision.Value ? item.ReadRevision : revision.Value), ct);
            if (updated == 0) return Results.NotFound();
            NotificationSources.StateChanged(db, userId, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
        group.MapPost("/{id:guid}/dismiss", async (Guid id, long? revision, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (revision is < 1) return BadRequest("invalid_notification_revision");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var query = Visible(db, userId).Where(item => item.Id == id);
            if (revision is null) query = query.Where(item => item.Type == NotificationType.TaskReminder);
            else query = query.Where(item => item.Revision == revision.Value);
            var now = clock.GetUtcNow();
            var updated = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.DismissedAt, now).SetProperty(item => item.ReadAt, now)
                .SetProperty(item => item.ReadRevision, item => item.Revision), ct);
            if (updated == 0) return Results.NotFound();
            NotificationSources.StateChanged(db, userId, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
        return endpoints;
    }

    internal static bool TryGetUserId(HttpContext context, out Guid userId)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);
    }
    private static string MakeCursor(DateTimeOffset time, Guid id) => $"{time.UtcTicks}_{id:N}";
    private static bool TryParseCursor(string? cursor, out DateTimeOffset time, out Guid id)
    {
        time = default; id = default;
        if (cursor is null) return true;
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) ||
            ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks ||
            !Guid.TryParseExact(parts[1], "N", out id)) return false;
        time = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
}
