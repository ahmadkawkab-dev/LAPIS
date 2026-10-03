namespace Wukna.Features.Chat;

public enum ChatBlobWorkPurpose { UploadReservation = 0, Delete = 1 }

// This record owns cleanup even after board/attachment metadata is deleted.
public sealed class ChatBlobWork
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChatBlobWorkPurpose Purpose { get; set; }
    public Guid BoardId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ClientMessageId { get; set; }
    public string OriginalStorageKey { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string PreviewStorageKey { get; set; } = string.Empty;
    public long ReservedBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
}
