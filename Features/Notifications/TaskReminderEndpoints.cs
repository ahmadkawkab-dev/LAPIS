namespace Wukna.Features.Notifications;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Tasks;
using Wukna.Shared.Data.AppDbContext;

public sealed record ReminderWriteRequest(int MinutesBefore);
public sealed record SnoozeRequest(int Minutes);
public sealed record TaskReminderDto(Guid TaskId, int MinutesBefore, DateTimeOffset DueAtUtc,
    DateTimeOffset? DeliveredAt);
public sealed record TaskNotificationDto(Guid TaskId, string TaskTitle, DateTimeOffset IssuedAt,
    DateTimeOffset? ReadAt, DateTimeOffset? DismissedAt);
public sealed record UpcomingReminderDto(Guid TaskId, string TaskTitle, DateTimeOffset DueAtUtc,
    int MinutesBefore);
public sealed record NotificationPageDto(IReadOnlyList<TaskNotificationDto> Items, string? NextCursor,
    int TotalCount, int UnreadCount);
public sealed record UpcomingReminderPageDto(IReadOnlyList<UpcomingReminderDto> Items,
    string? NextCursor, int TotalCount);

public static class TaskReminderEndpoints
{
    public static IEndpointRouteBuilder MapTaskReminderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tasks = endpoints.MapGroup("/api/tasks").RequireAuthorization();
        tasks.MapGet("/{id:guid}/reminder", async (Guid id, HttpContext context,
            WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!await db.PersonalTasks.AnyAsync(task => task.Id == id && task.UserId == userId, cancellationToken))
                return Results.NotFound();
            var reminder = await db.TaskReminders.AsNoTracking().SingleOrDefaultAsync(
                item => item.TaskId == id && item.UserId == userId, cancellationToken);
            return reminder is null ? Results.NoContent() : Results.Ok(ToDto(reminder));
        });
        tasks.MapPut("/{id:guid}/reminder", async (Guid id, ReminderWriteRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (request.MinutesBefore is < 0 or > 10080) return BadRequest("invalid_reminder_offset");
            var task = await db.PersonalTasks.SingleOrDefaultAsync(
                item => item.Id == id && item.UserId == userId, cancellationToken);
            if (task is null) return Results.NotFound();
            if (task.CompletedAt is not null || task.PlannedAtUtc is null)
                return BadRequest("reminder_requires_open_timed_task");
            var now = clock.GetUtcNow();
            var due = task.PlannedAtUtc.Value.AddMinutes(-request.MinutesBefore);
            if (due <= now) return BadRequest("reminder_time_in_past");
            var reminder = await db.TaskReminders.SingleOrDefaultAsync(item => item.TaskId == id, cancellationToken);
            if (reminder is null)
            {
                reminder = new TaskReminder { TaskId = id, UserId = userId, CreatedAt = now };
                db.TaskReminders.Add(reminder);
            }
            reminder.MinutesBefore = request.MinutesBefore;
            reminder.DueAtUtc = due;
            reminder.DeliveredAt = null;
            reminder.UpdatedAt = now;
            var notification = await db.TaskNotifications.SingleOrDefaultAsync(item => item.TaskId == id, cancellationToken);
            if (notification is not null) db.TaskNotifications.Remove(notification);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(ToDto(reminder));
        });
        tasks.MapDelete("/{id:guid}/reminder", async (Guid id, HttpContext context,
            WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!await db.PersonalTasks.AnyAsync(task => task.Id == id && task.UserId == userId, cancellationToken))
                return Results.NotFound();
            await db.TaskReminders.Where(item => item.TaskId == id && item.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.TaskNotifications.Where(item => item.TaskId == id && item.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            return Results.NoContent();
        });

        var notifications = endpoints.MapGroup("/api/notifications").RequireAuthorization();
        notifications.MapGet("/page", async (int? limit, string? cursor, bool? unreadOnly,
            HttpContext context, WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (limit is < 1 or > 100 || !TryParseCursor(cursor, out var cursorTime, out var cursorId))
                return BadRequest("invalid_notification_page");
            var size = limit ?? 30;
            var active = db.TaskNotifications.AsNoTracking()
                .Where(item => item.UserId == userId && item.DismissedAt == null);
            var totalCount = await active.CountAsync(cancellationToken);
            var unreadCount = await active.CountAsync(item => item.ReadAt == null, cancellationToken);
            var query = active;
            if (unreadOnly == true) query = query.Where(item => item.ReadAt == null);
            if (cursor is not null) query = query.Where(item => item.IssuedAt < cursorTime ||
                item.IssuedAt == cursorTime && item.TaskId.CompareTo(cursorId) < 0);
            var rows = await query.OrderByDescending(item => item.IssuedAt)
                .ThenByDescending(item => item.TaskId).Take(size + 1)
                .Select(item => new TaskNotificationDto(item.TaskId, item.Task.Title, item.IssuedAt,
                    item.ReadAt, item.DismissedAt)).ToArrayAsync(cancellationToken);
            var items = rows.Take(size).ToArray();
            var next = rows.Length > size ? MakeCursor(items[^1].IssuedAt, items[^1].TaskId) : null;
            return Results.Ok(new NotificationPageDto(items, next, totalCount, unreadCount));
        });
        notifications.MapGet("/upcoming/page", async (int? limit, string? cursor,
            HttpContext context, WuknaDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (limit is < 1 or > 100 || !TryParseCursor(cursor, out var cursorTime, out var cursorId))
                return BadRequest("invalid_notification_page");
            var now = clock.GetUtcNow();
            var end = now.AddDays(7);
            var active = db.TaskReminders.AsNoTracking()
                .Where(item => item.UserId == userId && item.DeliveredAt == null &&
                    item.DueAtUtc > now && item.DueAtUtc < end && item.Task.CompletedAt == null);
            var totalCount = await active.CountAsync(cancellationToken);
            var query = active;
            if (cursor is not null) query = query.Where(item => item.DueAtUtc > cursorTime ||
                item.DueAtUtc == cursorTime && item.TaskId.CompareTo(cursorId) > 0);
            var size = limit ?? 20;
            var rows = await query.OrderBy(item => item.DueAtUtc).ThenBy(item => item.TaskId)
                .Take(size + 1)
                .Select(item => new UpcomingReminderDto(item.TaskId, item.Task.Title,
                    item.DueAtUtc, item.MinutesBefore)).ToArrayAsync(cancellationToken);
            var items = rows.Take(size).ToArray();
            var next = rows.Length > size ? MakeCursor(items[^1].DueAtUtc, items[^1].TaskId) : null;
            return Results.Ok(new UpcomingReminderPageDto(items, next, totalCount));
        });
        notifications.MapGet("/", async (HttpContext context, WuknaDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var items = await db.TaskNotifications.AsNoTracking()
                .Where(item => item.UserId == userId && item.DismissedAt == null)
                .OrderByDescending(item => item.IssuedAt).Take(100)
                .Select(item => new TaskNotificationDto(item.TaskId, item.Task.Title, item.IssuedAt,
                    item.ReadAt, item.DismissedAt)).ToArrayAsync(cancellationToken);
            return Results.Ok(items);
        });
        notifications.MapGet("/upcoming", async (HttpContext context, WuknaDbContext db,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var now = clock.GetUtcNow();
            var end = now.AddDays(7);
            var items = await db.TaskReminders.AsNoTracking()
                .Where(item => item.UserId == userId && item.DeliveredAt == null &&
                    item.DueAtUtc > now && item.DueAtUtc < end && item.Task.CompletedAt == null)
                .OrderBy(item => item.DueAtUtc).Take(50)
                .Select(item => new UpcomingReminderDto(item.TaskId, item.Task.Title, item.DueAtUtc,
                    item.MinutesBefore)).ToArrayAsync(cancellationToken);
            return Results.Ok(items);
        });
        notifications.MapPost("/read-all", async (HttpContext context, WuknaDbContext db,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await db.TaskNotifications.Where(item => item.UserId == userId && item.ReadAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReadAt, clock.GetUtcNow()),
                    cancellationToken);
            return Results.NoContent();
        });
        notifications.MapPost("/{id:guid}/read", async (Guid id, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var updated = await db.TaskNotifications.Where(item => item.TaskId == id && item.UserId == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReadAt, clock.GetUtcNow()),
                    cancellationToken);
            return updated == 0 ? Results.NotFound() : Results.NoContent();
        });
        notifications.MapPost("/{id:guid}/dismiss", async (Guid id, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var now = clock.GetUtcNow();
            var updated = await db.TaskNotifications.Where(item => item.TaskId == id && item.UserId == userId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DismissedAt, now)
                    .SetProperty(item => item.ReadAt, now), cancellationToken);
            return updated == 0 ? Results.NotFound() : Results.NoContent();
        });
        notifications.MapPost("/{id:guid}/snooze", async (Guid id, SnoozeRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (request.Minutes is < 5 or > 1440) return BadRequest("invalid_snooze_duration");
            var reminder = await db.TaskReminders.Include(item => item.Task).SingleOrDefaultAsync(
                item => item.TaskId == id && item.UserId == userId, cancellationToken);
            var notification = await db.TaskNotifications.SingleOrDefaultAsync(
                item => item.TaskId == id && item.UserId == userId && item.DismissedAt == null,
                cancellationToken);
            if (reminder is null || notification is null) return Results.NotFound();
            if (reminder.Task.CompletedAt is not null) return BadRequest("completed_task_cannot_snooze");
            var now = clock.GetUtcNow();
            reminder.DueAtUtc = now.AddMinutes(request.Minutes);
            reminder.DeliveredAt = null;
            reminder.UpdatedAt = now;
            notification.ReadAt = now;
            notification.DismissedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });
        return endpoints;
    }

    public static async Task SyncForTask(WuknaDbContext db, PersonalTask task,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reminder = await db.TaskReminders.SingleOrDefaultAsync(
            item => item.TaskId == task.Id && item.UserId == task.UserId, cancellationToken);
        if (reminder is null) return;
        if (task.PlannedAtUtc is null || task.CompletedAt is not null)
        {
            db.TaskReminders.Remove(reminder);
            var stale = await db.TaskNotifications.SingleOrDefaultAsync(
                item => item.TaskId == task.Id && item.UserId == task.UserId, cancellationToken);
            if (stale is not null) db.TaskNotifications.Remove(stale);
            return;
        }
        var due = task.PlannedAtUtc.Value.AddMinutes(-reminder.MinutesBefore);
        if (due == reminder.DueAtUtc) return;
        reminder.DueAtUtc = due;
        reminder.DeliveredAt = null;
        reminder.UpdatedAt = now;
        var notification = await db.TaskNotifications.SingleOrDefaultAsync(
            item => item.TaskId == task.Id && item.UserId == task.UserId, cancellationToken);
        if (notification is not null) db.TaskNotifications.Remove(notification);
    }

    private static TaskReminderDto ToDto(TaskReminder item) =>
        new(item.TaskId, item.MinutesBefore, item.DueAtUtc, item.DeliveredAt);
    private static string MakeCursor(DateTimeOffset time, Guid id) =>
        $"{time.UtcTicks}_{id:N}";
    private static bool TryParseCursor(string? cursor, out DateTimeOffset time, out Guid id)
    {
        time = default;
        id = default;
        if (cursor is null) return true;
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) ||
            ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks ||
            !Guid.TryParseExact(parts[1], "N", out id)) return false;
        time = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);
    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
}
