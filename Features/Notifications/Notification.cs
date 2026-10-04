namespace Wukna.Features.Notifications;

using Wukna.Features.Tasks;

public enum NotificationType
{
    TaskReminder = 0,
    ChatActivity = 1,
    ScheduledTaskReminder = 2,
    SharedBoardActivity = 3,
    BoardInvitation = 4,
    TaskActivity = 5
}

public sealed class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public string? ActivityKind { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public Guid? BoardId { get; set; }
    // Board references are logical: revoked/deleted resources must not erase delivery history.
    // Every read filters against the current membership incarnation.
    public Guid? MembershipInstanceId { get; set; }
    public string? ResourceKind { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? ReminderGeneration { get; set; }
    public Guid? TaskId { get; set; }
    public PersonalTask? Task { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
    public long Revision { get; set; } = 1;
    public long ReadRevision { get; set; }
    public int ActivityCount { get; set; } = 1;
    public string? AggregationKey { get; set; }
    public long? FirstChatSequence { get; set; }
    public long? LastChatSequence { get; set; }
}
