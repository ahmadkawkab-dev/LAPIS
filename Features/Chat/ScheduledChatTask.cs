namespace Wukna.Features.Chat;

public sealed class ScheduledChatTask
{
    public Guid MessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset? EndsAtUtc { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public int OriginalOffsetMinutes { get; set; }
}
