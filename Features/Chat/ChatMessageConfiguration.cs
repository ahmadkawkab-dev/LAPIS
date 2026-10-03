namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> entity)
    {
        entity.ToTable("chat_messages", table =>
        {
            table.HasCheckConstraint("ck_chat_message_type", "type IN (0, 1, 2)");
            table.HasCheckConstraint("ck_chat_message_sequence", "sequence > 0");
            table.HasCheckConstraint("ck_chat_message_body",
                "(type = 0 AND body IS NOT NULL AND length(btrim(body)) BETWEEN 1 AND 4000) OR " +
                "(type = 1 AND (body IS NULL OR length(body) <= 2000)) OR (type = 2 AND body IS NULL)");
            table.HasCheckConstraint("ck_chat_message_client_id", "client_message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_chat_message_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
        });
        entity.HasKey(message => message.Id);
        entity.Property(message => message.Body).HasMaxLength(4000);
        entity.Property(message => message.RequestFingerprint).HasMaxLength(64).IsRequired();
        entity.Property(message => message.CreatedAt).HasDefaultValueSql("now()");
        entity.HasIndex(message => new { message.BoardId, message.Sequence }).IsUnique();
        entity.HasIndex(message => new { message.BoardId, message.SenderUserId, message.ClientMessageId }).IsUnique();
        entity.HasOne(message => message.Board).WithMany()
            .HasForeignKey(message => message.BoardId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(message => message.SenderUser).WithMany()
            .HasForeignKey(message => message.SenderUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
