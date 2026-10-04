namespace Wukna.Features.Notifications;

using Wukna.Features.Tasks;

public sealed class TaskReminder
{
    public Guid TaskId { get; set; }
    public PersonalTask Task { get; set; } = null!;
    public Guid UserId { get; set; }
    public Guid ScheduleGeneration { get; set; } = Guid.NewGuid();
    public int MinutesBefore { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
