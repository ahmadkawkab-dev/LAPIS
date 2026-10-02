namespace Wukna.Features.Tasks;

using Wukna.Features.Users;

public sealed class PlanningSettings
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TimeZoneId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
