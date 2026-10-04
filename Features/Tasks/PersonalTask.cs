namespace Wukna.Features.Tasks;

using Wukna.Features.Users;

public sealed class PersonalTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid? ListId { get; set; }
    public PersonalTaskList? List { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly? PlannedDate { get; set; }
    public TimeOnly? PlannedTime { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTimeOffset? PlannedAtUtc { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
