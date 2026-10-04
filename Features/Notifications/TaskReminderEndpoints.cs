namespace Wukna.Features.Notifications;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Tasks;
using Wukna.Shared.Data.AppDbContext;

public sealed record ReminderWriteRequest(int MinutesBefore);
public sealed record SnoozeRequest(int Minutes);
public sealed record TaskReminderDto(Guid TaskId, int MinutesBefore, DateTimeOffset DueAtUtc,
    DateTimeOffset? DeliveredAt);
public sealed record UpcomingReminderDto(Guid TaskId, string TaskTitle, DateTimeOffset DueAtUtc,
    int MinutesBefore, string ResourceKind = "task");
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
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var tasks = await db.PersonalTasks.FromSqlInterpolated($"SELECT * FROM personal_tasks WHERE id = {id} AND user_id = {userId} FOR UPDATE").ToArrayAsync(cancellationToken);
            var task = tasks.SingleOrDefault();
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
            reminder.ScheduleGeneration = Guid.NewGuid();
            reminder.UpdatedAt = now;
            var notification = await db.Notifications.SingleOrDefaultAsync(item => item.TaskId == id, cancellationToken);
            if (notification is not null) db.Notifications.Remove(notification);
            NotificationSources.StateChanged(db, userId, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(ToDto(reminder));
        });
        tasks.MapDelete("/{id:guid}/reminder", async (Guid id, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var owned = await db.PersonalTasks.FromSqlInterpolated($"SELECT * FROM personal_tasks WHERE id = {id} AND user_id = {userId} FOR UPDATE").AsNoTracking().ToArrayAsync(cancellationToken);
            if (owned.Length == 0) return Results.NotFound();
            await db.TaskReminders.Where(item => item.TaskId == id && item.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.Notifications.Where(item => item.TaskId == id && item.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            NotificationSources.StateChanged(db, userId, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        });

        var notifications = endpoints.MapGroup("/api/notifications").RequireAuthorization();
        notifications.MapGet("/upcoming/page", async (int? limit, string? cursor,
            HttpContext context, WuknaDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (limit is < 1 or > 100 || !TryParseCursor(cursor, out var cursorTime, out var cursorId))
                return BadRequest("invalid_notification_page");
            var now = clock.GetUtcNow();
            var end = now.AddDays(7);
            var active = Upcoming(db, userId, now, end);
            var totalCount = await active.CountAsync(cancellationToken);
            var query = active;
            if (cursor is not null) query = query.Where(item => item.DueAtUtc > cursorTime ||
                item.DueAtUtc == cursorTime && item.Id.CompareTo(cursorId) > 0);
            var size = limit ?? 20;
            var result = await query.OrderBy(item => item.DueAtUtc).ThenBy(item => item.Id).Take(size + 1).ToArrayAsync(cancellationToken);
            var rows = result.Select(item => new UpcomingReminderDto(item.Id, item.Title, item.DueAtUtc, item.MinutesBefore, item.ResourceKind)).ToArray();
            var items = rows.Take(size).ToArray();
            var next = rows.Length > size ? MakeCursor(items[^1].DueAtUtc, items[^1].TaskId) : null;
            return Results.Ok(new UpcomingReminderPageDto(items, next, totalCount));
        });
        notifications.MapGet("/upcoming", async (HttpContext context, WuknaDbContext db,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var now = clock.GetUtcNow();
            var end = now.AddDays(7);
            var rows = await Upcoming(db, userId, now, end).OrderBy(item => item.DueAtUtc).ThenBy(item => item.Id).Take(50).ToArrayAsync(cancellationToken);
            var items = rows.Select(item => new UpcomingReminderDto(item.Id, item.Title, item.DueAtUtc, item.MinutesBefore, item.ResourceKind)).ToArray();
            return Results.Ok(items);
        });
        notifications.MapPost("/{id:guid}/snooze", async (Guid id, SnoozeRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (request.Minutes is < 5 or > 1440) return BadRequest("invalid_snooze_duration");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var owned = await db.PersonalTasks.FromSqlInterpolated($"SELECT * FROM personal_tasks WHERE id = {id} AND user_id = {userId} FOR UPDATE").ToArrayAsync(cancellationToken);
            if (owned.Length == 0) return Results.NotFound();
            var reminder = await db.TaskReminders.Include(item => item.Task).SingleOrDefaultAsync(
                item => item.TaskId == id && item.UserId == userId, cancellationToken);
            var notification = await db.Notifications.SingleOrDefaultAsync(
                item => item.TaskId == id && item.UserId == userId && item.DismissedAt == null,
                cancellationToken);
            if (reminder is null || notification is null) return Results.NotFound();
            if (reminder.Task.CompletedAt is not null) return BadRequest("completed_task_cannot_snooze");
            var now = clock.GetUtcNow();
            reminder.DueAtUtc = now.AddMinutes(request.Minutes);
            reminder.DeliveredAt = null;
            reminder.ScheduleGeneration = Guid.NewGuid();
            reminder.UpdatedAt = now;
            notification.ReadAt = now;
            notification.DismissedAt = now;
            notification.ReadRevision = notification.Revision;
            NotificationSources.StateChanged(db, userId, now);
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        });
        return endpoints;
    }

    private sealed class UpcomingRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public DateTimeOffset DueAtUtc { get; set; }
        public int MinutesBefore { get; set; }
        public string ResourceKind { get; set; } = "";
    }
    private static IQueryable<UpcomingRow> Upcoming(WuknaDbContext db, Guid userId, DateTimeOffset now, DateTimeOffset end) =>
        db.TaskReminders.AsNoTracking().Where(item => item.UserId == userId && item.DeliveredAt == null &&
          item.DueAtUtc > now && item.DueAtUtc < end && item.Task.CompletedAt == null)
        .Select(item => new UpcomingRow { Id = item.TaskId, Title = item.Task.Title, DueAtUtc = item.DueAtUtc,
            MinutesBefore = item.MinutesBefore, ResourceKind = "task" })
        .Concat(db.CalendarEventReminders.AsNoTracking().Where(item => item.UserId == userId && item.DeliveredAt == null &&
            item.DueAtUtc > now && item.DueAtUtc < end)
          .Select(item => new UpcomingRow { Id = item.CalendarEventId, Title = item.CalendarEvent.Title, DueAtUtc = item.DueAtUtc,
            MinutesBefore = item.MinutesBefore, ResourceKind = "calendarEvent" }));

    public static async Task SyncForTask(WuknaDbContext db, PersonalTask task,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reminder = await db.TaskReminders.SingleOrDefaultAsync(
            item => item.TaskId == task.Id && item.UserId == task.UserId, cancellationToken);
        if (reminder is null) return;
        NotificationSources.StateChanged(db, task.UserId, now);
        if (task.PlannedAtUtc is null || task.CompletedAt is not null)
        {
            db.TaskReminders.Remove(reminder);
            var stale = await db.Notifications.SingleOrDefaultAsync(
                item => item.TaskId == task.Id && item.UserId == task.UserId, cancellationToken);
            if (stale is not null) db.Notifications.Remove(stale);
            return;
        }
        var due = task.PlannedAtUtc.Value.AddMinutes(-reminder.MinutesBefore);
        if (due == reminder.DueAtUtc) return;
        reminder.DueAtUtc = due;
        reminder.DeliveredAt = null;
        reminder.ScheduleGeneration = Guid.NewGuid();
        reminder.UpdatedAt = now;
        var notification = await db.Notifications.SingleOrDefaultAsync(
            item => item.TaskId == task.Id && item.UserId == task.UserId, cancellationToken);
        if (notification is not null) db.Notifications.Remove(notification);
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
