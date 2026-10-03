namespace Wukna.Features.Chat;

public enum ChatAttachmentScanStatus
{
    Pending = 0, Scanning = 1, Available = 2, Rejected = 3, ScanFailed = 4
}

public sealed class ChatAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;
    public string OriginalStorageKey { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string PreviewStorageKey { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/webp";
    public long InputByteSize { get; set; }
    public long ByteSize { get; set; }
    public long PreviewByteSize { get; set; }
    public long StoredByteSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public ChatAttachmentScanStatus ScanStatus { get; set; }
    public int ScanAttempts { get; set; }
    public DateTimeOffset NextScanAttemptAt { get; set; }
    public Guid? ScanLeaseToken { get; set; }
    public DateTimeOffset? ScanLeaseExpiresAt { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
