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
                      FOR UPDATE OF reminder SKIP LOCKED
                    ), delivered AS (
                      UPDATE task_reminders AS reminder
                      SET delivered_at = {now}, updated_at = {now}
                      FROM due WHERE reminder.task_id = due.task_id
                      RETURNING reminder.task_id, reminder.user_id
                    )
                    INSERT INTO task_notifications
                      (task_id, user_id, issued_at, read_at, dismissed_at)
                    SELECT task_id, user_id, {now}, NULL, NULL FROM delivered
                    ON CONFLICT (task_id) DO UPDATE
                      SET issued_at = EXCLUDED.issued_at, read_at = NULL, dismissed_at = NULL
                    """, cancellationToken);
}
