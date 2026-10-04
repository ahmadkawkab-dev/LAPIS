namespace Wukna.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed class TaskReminderWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<TaskReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
                var now = clock.GetUtcNow();
                var issued = await ProcessDue(db, now, stoppingToken);
                issued += await CalendarReminderEndpoints.ProcessDue(db, now, stoppingToken);
                if (issued > 0) logger.LogInformation("Processed {Count} due task reminders", issued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Task reminder processing failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static Task<int> ProcessDue(WuknaDbContext db, DateTimeOffset now,
        CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync($"""
                    WITH due AS (
                      SELECT reminder.task_id
                      FROM task_reminders AS reminder
                      JOIN personal_tasks AS task ON task.id = reminder.task_id
                      WHERE reminder.delivered_at IS NULL
                        AND reminder.due_at_utc <= {now}
                        AND task.completed_at IS NULL
                      ORDER BY reminder.due_at_utc
                      LIMIT 100
                      FOR UPDATE OF task, reminder SKIP LOCKED
                    ), delivered AS (
                      UPDATE task_reminders AS reminder
                      SET delivered_at = {now}, updated_at = {now}
                      FROM due WHERE reminder.task_id = due.task_id
                      RETURNING reminder.task_id, reminder.user_id, reminder.schedule_generation
                    ), notified AS (
                    INSERT INTO notifications
                      (id, task_id, user_id, type, title, resource_kind, resource_id,
                       issued_at, updated_at, read_at, dismissed_at, revision, read_revision, activity_count, reminder_generation)
                    SELECT delivered.task_id, delivered.task_id, delivered.user_id, 0, task.title,
                      'task', delivered.task_id, {now}, {now}, NULL, NULL, 1, 0, 1, delivered.schedule_generation
                    FROM delivered JOIN personal_tasks AS task ON task.id = delivered.task_id
                    LEFT JOIN notification_preferences AS preference ON preference.user_id = delivered.user_id
                    WHERE COALESCE(preference.task_reminder_notifications_enabled, TRUE)
                    ON CONFLICT (id) DO UPDATE
                      SET issued_at = EXCLUDED.issued_at, updated_at = EXCLUDED.updated_at,
                          title = EXCLUDED.title, revision = notifications.revision + 1,
                          read_at = NULL, dismissed_at = NULL, read_revision = 0,
                          reminder_generation = EXCLUDED.reminder_generation
                    RETURNING id, user_id, revision
                    )
                    INSERT INTO notification_work
                      (id, kind, user_id, source_event_key, type, notification_id, notification_revision,
                       created_at, next_attempt_at, expires_at, attempts)
                    SELECT gen_random_uuid(), 1, user_id, 'notification:' || replace(id::text, '-', '') || ':' || revision,
                      0, id, revision, {now}, {now}, {now.AddHours(1)}, 0 FROM notified
                    ON CONFLICT (user_id, source_event_key, kind) DO NOTHING
                    """, cancellationToken);
}
