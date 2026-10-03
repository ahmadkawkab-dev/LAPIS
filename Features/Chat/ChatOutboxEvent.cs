namespace Wukna.Features.Chat;

public enum ChatOutboxEventKind
{
    MessageCreated = 0, SettingsChanged = 1, MemberStateChanged = 2,
    AttachmentChanged = 3, ReadCursorChanged = 4, AccessRevoked = 5
}

// Operational references deliberately have no cascading FKs: revocation must
// survive deletion of the membership or board. No message body is duplicated here.
public sealed class ChatOutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public ChatOutboxEventKind Kind { get; set; }
    public int EventVersion { get; set; } = 1;
    public Guid? MessageId { get; set; }
    public long? MessageSequence { get; set; }
    public Guid? MemberUserId { get; set; }
    public Guid? MembershipInstanceId { get; set; }
    public Guid? AttachmentId { get; set; }
    public long? Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastErrorCode { get; set; }
}
