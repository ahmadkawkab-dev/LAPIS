namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ChatBlobWorkConfiguration : IEntityTypeConfiguration<ChatBlobWork>
{
    public void Configure(EntityTypeBuilder<ChatBlobWork> entity)
    {
        entity.ToTable("chat_blob_work", table =>
        {
            table.HasCheckConstraint("ck_chat_blob_work_purpose", "purpose IN (0, 1)");
            table.HasCheckConstraint("ck_chat_blob_work_counters", "reserved_bytes >= 0 AND attempts >= 0");
            table.HasCheckConstraint("ck_chat_blob_work_reservation",
                "purpose <> 0 OR (user_id IS NOT NULL AND client_message_id IS NOT NULL AND reserved_bytes > 0)");
            table.HasCheckConstraint("ck_chat_blob_work_lease",
                "(lease_token IS NULL AND lease_expires_at IS NULL) OR " +
                "(lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");
        });
        entity.HasKey(work => work.Id);
        entity.Property(work => work.OriginalStorageKey).HasMaxLength(200).IsRequired();
        entity.Property(work => work.StorageKey).HasMaxLength(200).IsRequired();
        entity.Property(work => work.PreviewStorageKey).HasMaxLength(200).IsRequired();
        entity.Property(work => work.LastErrorCode).HasMaxLength(100);
        entity.Property(work => work.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(work => new { work.DueAt, work.Id });
        entity.HasIndex(work => new { work.BoardId, work.UserId, work.ClientMessageId })
            .IsUnique().HasDatabaseName("ix_chat_blob_work_upload_reservation").HasFilter("purpose = 0");
    }
}
