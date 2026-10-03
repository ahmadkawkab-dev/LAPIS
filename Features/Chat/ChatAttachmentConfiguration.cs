namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ChatAttachmentConfiguration : IEntityTypeConfiguration<ChatAttachment>
{
    public void Configure(EntityTypeBuilder<ChatAttachment> entity)
    {
        entity.ToTable("chat_attachments", table =>
        {
            table.HasCheckConstraint("ck_chat_attachment_status", "scan_status IN (0, 1, 2, 3, 4)");
            table.HasCheckConstraint("ck_chat_attachment_sizes",
                "input_byte_size > 0 AND byte_size > 0 AND preview_byte_size > 0 " +
                "AND stored_byte_size >= byte_size + preview_byte_size");
            table.HasCheckConstraint("ck_chat_attachment_dimensions", "width > 0 AND height > 0");
            table.HasCheckConstraint("ck_chat_attachment_attempts", "scan_attempts >= 0");
            table.HasCheckConstraint("ck_chat_attachment_lease",
                "(scan_status = 1 AND scan_lease_token IS NOT NULL AND scan_lease_expires_at IS NOT NULL) OR " +
                "(scan_status <> 1 AND scan_lease_token IS NULL AND scan_lease_expires_at IS NULL)");
            table.HasCheckConstraint("ck_chat_attachment_available_scan", "scan_status <> 2 OR scanned_at IS NOT NULL");
        });
        entity.HasKey(attachment => attachment.Id);
        entity.Property(attachment => attachment.OriginalStorageKey).HasMaxLength(200).IsRequired();
        entity.Property(attachment => attachment.StorageKey).HasMaxLength(200).IsRequired();
        entity.Property(attachment => attachment.PreviewStorageKey).HasMaxLength(200).IsRequired();
        entity.Property(attachment => attachment.OriginalFileName).HasMaxLength(255).IsRequired();
        entity.Property(attachment => attachment.ContentType).HasMaxLength(100).IsRequired();
        entity.Property(attachment => attachment.LastErrorCode).HasMaxLength(100);
        entity.Property(attachment => attachment.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(attachment => attachment.NextScanAttemptAt).HasDefaultValueSql("now()");
        entity.HasOne(attachment => attachment.Message).WithOne(message => message.Attachment)
            .HasForeignKey<ChatAttachment>(attachment => attachment.MessageId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(attachment => new { attachment.NextScanAttemptAt, attachment.CreatedAt, attachment.Id })
            .HasDatabaseName("ix_chat_attachments_scan_work").HasFilter("scan_status IN (0, 1, 4)");
    }
}
