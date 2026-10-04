namespace Wukna.Features.Calendar;

using Wukna.Features.Users;

public sealed class CalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    // Snapshot provenance, retained if the original board/message is deleted.
    // Only the authorized server-side import sets this reference.
    public Guid? SourceChatMessageId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public bool IsAllDay { get; set; }
    public DateOnly? AllDayStartDate { get; set; }
    public DateOnly? AllDayEndDateExclusive { get; set; }
    public DateTime? LocalStart { get; set; }
    public DateTime? LocalEnd { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTimeOffset? StartAtUtc { get; set; }
    public DateTimeOffset? EndAtUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
