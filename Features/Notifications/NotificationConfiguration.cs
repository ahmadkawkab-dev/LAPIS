namespace Wukna.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wukna.Features.Users;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> entity)
    {
        entity.ToTable("notifications", table =>
        {
            table.HasCheckConstraint("ck_notification_type", "type BETWEEN 0 AND 5");
            table.HasCheckConstraint("ck_notification_revisions",
                "revision > 0 AND read_revision >= 0 AND read_revision <= revision AND activity_count > 0");
            table.HasCheckConstraint("ck_notification_board_instance",
                "(board_id IS NULL AND membership_instance_id IS NULL) OR " +
                "(board_id IS NOT NULL AND membership_instance_id IS NOT NULL AND " +
                "membership_instance_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
            table.HasCheckConstraint("ck_notification_task_reference",
                "(type = 0 AND task_id IS NOT NULL AND id = task_id) OR (type <> 0 AND task_id IS NULL)");
            table.HasCheckConstraint("ck_notification_chat_sequences",
                "(first_chat_sequence IS NULL AND last_chat_sequence IS NULL) OR " +
                "(first_chat_sequence IS NOT NULL AND last_chat_sequence IS NOT NULL AND first_chat_sequence > 0 AND last_chat_sequence >= first_chat_sequence)");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Title).HasMaxLength(500).IsRequired();
        entity.Property(item => item.ActivityKind).HasMaxLength(50);
        entity.Property(item => item.ResourceKind).HasMaxLength(50);
        entity.Property(item => item.AggregationKey).HasMaxLength(200);
        entity.HasOne<User>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(item => item.Task).WithMany().HasForeignKey(item => item.TaskId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(item => item.TaskId).IsUnique().HasFilter("task_id IS NOT NULL");
        entity.HasIndex(item => new { item.UserId, item.IssuedAt, item.Id });
        entity.HasIndex(item => new { item.UserId, item.IssuedAt, item.Id }, "ix_notifications_unread")
            .HasDatabaseName("ix_notifications_unread").HasFilter("dismissed_at IS NULL AND read_revision < revision");
        entity.HasIndex(item => new { item.UserId, item.AggregationKey }).IsUnique()
            .HasFilter("aggregation_key IS NOT NULL AND dismissed_at IS NULL");
        entity.HasIndex(item => new { item.BoardId, item.UserId, item.MembershipInstanceId });
    }
}

public sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> entity)
    {
        entity.ToTable("notification_preferences", table =>
        {
            table.HasCheckConstraint("ck_notification_preference_volume", "sound_volume BETWEEN 0 AND 1");
            table.HasCheckConstraint("ck_notification_preference_revision", "revision >= 0");
        });
        entity.HasKey(item => item.UserId);
        entity.HasOne<User>().WithOne().HasForeignKey<NotificationPreference>(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BoardNotificationPreferenceConfiguration : IEntityTypeConfiguration<BoardNotificationPreference>
{
    public void Configure(EntityTypeBuilder<BoardNotificationPreference> entity)
    {
        entity.ToTable("board_notification_preferences", table =>
        {
            table.HasCheckConstraint("ck_board_notification_preference_mode", "mode BETWEEN 0 AND 2");
            table.HasCheckConstraint("ck_board_notification_preference_revision", "revision >= 0");
        });
        entity.HasKey(item => new { item.BoardId, item.UserId });
        entity.HasOne(item => item.Membership).WithOne()
            .HasForeignKey<BoardNotificationPreference>(item => new { item.BoardId, item.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
