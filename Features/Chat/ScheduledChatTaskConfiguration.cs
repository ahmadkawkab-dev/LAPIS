namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ScheduledChatTaskConfiguration : IEntityTypeConfiguration<ScheduledChatTask>
{
    public void Configure(EntityTypeBuilder<ScheduledChatTask> entity)
    {
        entity.ToTable("scheduled_chat_tasks", table =>
        {
            table.HasCheckConstraint("ck_scheduled_chat_task_end", "ends_at_utc IS NULL OR ends_at_utc > starts_at_utc");
            table.HasCheckConstraint("ck_scheduled_chat_task_offset", "original_offset_minutes BETWEEN -840 AND 840");
            table.HasCheckConstraint("ck_scheduled_chat_task_title", "length(btrim(title)) > 0");
            table.HasCheckConstraint("ck_scheduled_chat_task_zone", "length(btrim(time_zone_id)) > 0");
        });
        entity.HasKey(task => task.MessageId);
        entity.Property(task => task.MessageId).ValueGeneratedNever();
        entity.Property(task => task.Title).HasMaxLength(200).IsRequired();
        entity.Property(task => task.Description).HasMaxLength(4000);
        entity.Property(task => task.TimeZoneId).HasMaxLength(100).IsRequired();
        entity.HasOne(task => task.Message).WithOne(message => message.ScheduledTask)
            .HasForeignKey<ScheduledChatTask>(task => task.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
