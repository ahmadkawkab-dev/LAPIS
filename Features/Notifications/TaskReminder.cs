namespace Wukna.Features.Notifications;

using Wukna.Features.Tasks;

public sealed class TaskReminder
{
    public Guid TaskId { get; set; }
    public PersonalTask Task { get; set; } = null!;
    public Guid UserId { get; set; }
    public int MinutesBefore { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TaskNotification
{
    public Guid TaskId { get; set; }
    public PersonalTask Task { get; set; } = null!;
    public Guid UserId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}
