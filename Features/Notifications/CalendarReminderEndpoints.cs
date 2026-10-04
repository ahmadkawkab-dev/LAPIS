namespace Wukna.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wukna.Features.Calendar;
using Wukna.Shared.Data.AppDbContext;

public sealed class CalendarEventReminder
{
    public Guid CalendarEventId { get; set; }
    public CalendarEvent CalendarEvent { get; set; } = null!;
    public Guid UserId { get; set; }
    public int MinutesBefore { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public Guid ScheduleGeneration { get; set; } = Guid.NewGuid();
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class CalendarEventReminderConfiguration : IEntityTypeConfiguration<CalendarEventReminder>
{
    public void Configure(EntityTypeBuilder<CalendarEventReminder> entity)
    {
        entity.ToTable("calendar_event_reminders", table => table.HasCheckConstraint("ck_calendar_reminder_minutes", "minutes_before BETWEEN 0 AND 10080"));
        entity.HasKey(item => item.CalendarEventId);
        entity.HasOne(item => item.CalendarEvent).WithOne().HasForeignKey<CalendarEventReminder>(item => item.CalendarEventId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(item => item.DueAtUtc).HasFilter("delivered_at IS NULL");
        entity.HasIndex(item => new { item.UserId, item.DueAtUtc });
    }
}
public sealed record CalendarReminderDto(Guid CalendarEventId, int MinutesBefore, DateTimeOffset DueAtUtc, DateTimeOffset? DeliveredAt);

public static class CalendarReminderEndpoints
{
    public static IEndpointRouteBuilder MapCalendarReminderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/calendar/events/{id:guid}/reminder").RequireAuthorization();
        group.MapGet("/", async (Guid id, HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!await db.CalendarEvents.AnyAsync(item => item.Id == id && item.UserId == userId, ct)) return Results.NotFound();
            var reminder = await db.CalendarEventReminders.AsNoTracking().SingleOrDefaultAsync(item => item.CalendarEventId == id && item.UserId == userId, ct);
            return reminder is null ? Results.NoContent() : Results.Ok(ToDto(reminder));
        });
        group.MapPut("/", async (Guid id, ReminderWriteRequest request, HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (request.MinutesBefore is < 0 or > 10080) return Results.BadRequest(new { code = "invalid_reminder_offset" });
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var events = await db.CalendarEvents.FromSqlInterpolated($"SELECT * FROM calendar_events WHERE id = {id} AND user_id = {userId} FOR UPDATE").ToArrayAsync(ct);
            var item = events.SingleOrDefault();
            if (item is null) return Results.NotFound();
            if (item.SourceChatMessageId is null || item.StartAtUtc is null) return Results.BadRequest(new { code = "reminder_requires_imported_calendar_task" });
            var now = clock.GetUtcNow(); var due = item.StartAtUtc.Value.AddMinutes(-request.MinutesBefore);
            if (due <= now) return Results.BadRequest(new { code = "reminder_time_in_past" });
            var reminder = await db.CalendarEventReminders.SingleOrDefaultAsync(item => item.CalendarEventId == id, ct);
            if (reminder is null) { reminder = new() { CalendarEventId = id, UserId = userId, CreatedAt = now }; db.CalendarEventReminders.Add(reminder); }
            reminder.MinutesBefore = request.MinutesBefore; reminder.DueAtUtc = due; reminder.UpdatedAt = now;
            reminder.DeliveredAt = null; reminder.ScheduleGeneration = Guid.NewGuid();
            await ClearNotifications(db, id, userId, ct);
            NotificationSources.StateChanged(db, userId, now);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Results.Ok(ToDto(reminder));
        });
        group.MapDelete("/", async (Guid id, HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var owned = await db.CalendarEvents.FromSqlInterpolated($"SELECT * FROM calendar_events WHERE id = {id} AND user_id = {userId} FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
            if (owned.Length == 0) return Results.NotFound();
            await db.CalendarEventReminders.Where(item => item.CalendarEventId == id && item.UserId == userId).ExecuteDeleteAsync(ct);
            await ClearNotifications(db, id, userId, ct);
            NotificationSources.StateChanged(db, userId, clock.GetUtcNow());
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
        });
        group.MapPost("/snooze", async (Guid id, SnoozeRequest request, HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (request.Minutes is < 5 or > 1440) return Results.BadRequest(new { code = "invalid_snooze_duration" });
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var owned = await db.CalendarEvents.FromSqlInterpolated($"SELECT * FROM calendar_events WHERE id = {id} AND user_id = {userId} FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
            if (owned.Length == 0) return Results.NotFound();
            var reminder = await db.CalendarEventReminders.SingleOrDefaultAsync(item => item.CalendarEventId == id && item.UserId == userId && item.DeliveredAt != null, ct);
            if (reminder is null) return Results.NotFound();
            var now = clock.GetUtcNow(); reminder.DueAtUtc = now.AddMinutes(request.Minutes); reminder.DeliveredAt = null;
            reminder.ScheduleGeneration = Guid.NewGuid(); reminder.UpdatedAt = now;
            await ClearNotifications(db, id, userId, ct); NotificationSources.StateChanged(db, userId, now);
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.NoContent();
        });
        return endpoints;
    }

    public static async Task Sync(WuknaDbContext db, CalendarEvent item, DateTimeOffset now, CancellationToken ct)
    {
        var reminder = await db.CalendarEventReminders.SingleOrDefaultAsync(candidate => candidate.CalendarEventId == item.Id, ct);
        if (reminder is null) return;
        if (item.StartAtUtc is null || item.IsAllDay) db.CalendarEventReminders.Remove(reminder);
        else if (reminder.DueAtUtc != item.StartAtUtc.Value.AddMinutes(-reminder.MinutesBefore))
        {
            reminder.DueAtUtc = item.StartAtUtc.Value.AddMinutes(-reminder.MinutesBefore); reminder.DeliveredAt = null;
            reminder.ScheduleGeneration = Guid.NewGuid(); reminder.UpdatedAt = now;
        }
        else return;
        await ClearNotifications(db, item.Id, item.UserId, ct); NotificationSources.StateChanged(db, item.UserId, now);
    }
    public static Task<int> ClearNotifications(WuknaDbContext db, Guid id, Guid userId, CancellationToken ct) =>
        db.Notifications.Where(item => item.UserId == userId && item.Type == NotificationType.ScheduledTaskReminder &&
            item.ResourceKind == "calendarEvent" && item.ResourceId == id).ExecuteDeleteAsync(ct);

    public static Task<int> ProcessDue(WuknaDbContext db, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH due AS (
              SELECT reminder.calendar_event_id FROM calendar_event_reminders AS reminder
              JOIN calendar_events AS event ON event.id = reminder.calendar_event_id
              WHERE reminder.delivered_at IS NULL AND reminder.due_at_utc <= {now}
                AND event.source_chat_message_id IS NOT NULL AND event.start_at_utc IS NOT NULL
              ORDER BY reminder.due_at_utc LIMIT 100 FOR UPDATE OF event, reminder SKIP LOCKED
            ), delivered AS (
              UPDATE calendar_event_reminders AS reminder SET delivered_at = {now}, updated_at = {now}
              FROM due WHERE reminder.calendar_event_id = due.calendar_event_id RETURNING reminder.*
            ), notified AS (
              INSERT INTO notifications (id, user_id, type, title, resource_kind, resource_id, issued_at, updated_at,
                 revision, read_revision, activity_count, reminder_generation)
              SELECT gen_random_uuid(), delivered.user_id, 2, event.title, 'calendarEvent', event.id,
                {now}, {now}, 1, 0, 1, delivered.schedule_generation
              FROM delivered JOIN calendar_events AS event ON event.id = delivered.calendar_event_id
              LEFT JOIN notification_preferences AS preference ON preference.user_id = delivered.user_id
              WHERE COALESCE(preference.scheduled_task_reminder_notifications_enabled, TRUE)
              RETURNING id, user_id, revision
            ) INSERT INTO notification_work (id, kind, user_id, source_event_key, type, notification_id,
              notification_revision, created_at, next_attempt_at, expires_at, attempts)
            SELECT gen_random_uuid(), 1, user_id, 'notification:' || replace(id::text, '-', '') || ':1',
              2, id, revision, {now}, {now}, {now.AddHours(1)}, 0 FROM notified
            ON CONFLICT (user_id, source_event_key, kind) DO NOTHING
            """, ct);
    private static CalendarReminderDto ToDto(CalendarEventReminder item) => new(item.CalendarEventId, item.MinutesBefore, item.DueAtUtc, item.DeliveredAt);
}
