namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ChatOutboxEventConfiguration : IEntityTypeConfiguration<ChatOutboxEvent>
{
    public void Configure(EntityTypeBuilder<ChatOutboxEvent> entity)
    {
        entity.ToTable("chat_outbox_events", table =>
        {
            table.HasCheckConstraint("ck_chat_outbox_kind", "kind IN (0, 1, 2, 3, 4, 5)");
            table.HasCheckConstraint("ck_chat_outbox_counters",
                "event_version > 0 AND attempts >= 0 AND (revision IS NULL OR revision >= 0) " +
                "AND (message_sequence IS NULL OR message_sequence >= 0)");
            table.HasCheckConstraint("ck_chat_outbox_lease",
                "(lease_token IS NULL AND lease_expires_at IS NULL) OR " +
                "(lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND processed_at IS NULL)");
            table.HasCheckConstraint("ck_chat_outbox_payload",
                "(kind = 0 AND message_id IS NOT NULL AND message_sequence IS NOT NULL AND message_sequence > 0) OR " +
                "(kind = 1 AND revision IS NOT NULL) OR " +
                "(kind = 2 AND member_user_id IS NOT NULL AND revision IS NOT NULL) OR " +
                "(kind = 3 AND attachment_id IS NOT NULL AND message_id IS NOT NULL) OR " +
                "(kind = 4 AND member_user_id IS NOT NULL AND message_sequence IS NOT NULL) OR " +
                "(kind = 5 AND ((member_user_id IS NULL AND membership_instance_id IS NULL) OR " +
                "(member_user_id IS NOT NULL AND membership_instance_id IS NOT NULL)))");
        });
        entity.HasKey(work => work.Id);
        entity.Property(work => work.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(work => work.NextAttemptAt).HasDefaultValueSql("now()");
        entity.Property(work => work.LastErrorCode).HasMaxLength(100);
        entity.HasIndex(work => new { work.NextAttemptAt, work.CreatedAt, work.Id })
            .HasDatabaseName("ix_chat_outbox_events_pending").HasFilter("processed_at IS NULL");
    }
}
