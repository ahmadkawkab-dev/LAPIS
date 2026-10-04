namespace Wukna.Features.Chat;

using Wukna.Features.Board;
using Wukna.Features.Users;

public enum ChatMessageType { Text = 0, Attachment = 1, ScheduledTask = 2 }

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public Board Board { get; set; } = null!;
    public Guid SenderUserId { get; set; }
    public User SenderUser { get; set; } = null!;
    public long Sequence { get; set; }
    public ChatMessageType Type { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid ClientMessageId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public string MentionsJson { get; set; } = "[]";
    public Guid? ReplyToMessageId { get; set; }
    public Guid? ReplyAuthorUserId { get; set; }
    public bool NotifyReplyAuthor { get; set; }
    public ChatAttachment? Attachment { get; set; }
    public ScheduledChatTask? ScheduledTask { get; set; }
}
