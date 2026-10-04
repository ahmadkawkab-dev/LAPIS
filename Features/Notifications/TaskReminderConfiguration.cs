namespace Wukna.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskReminderConfiguration : IEntityTypeConfiguration<TaskReminder>
{
    public void Configure(EntityTypeBuilder<TaskReminder> entity)
    {
        entity.ToTable("task_reminders", table => table.HasCheckConstraint(
            "ck_task_reminder_minutes", "minutes_before BETWEEN 0 AND 10080"));
        entity.HasKey(reminder => reminder.TaskId);
        entity.HasOne(reminder => reminder.Task).WithOne()
            .HasForeignKey<TaskReminder>(reminder => reminder.TaskId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(reminder => new { reminder.DeliveredAt, reminder.DueAtUtc });
        entity.HasIndex(reminder => new { reminder.UserId, reminder.DueAtUtc });
    }
}
