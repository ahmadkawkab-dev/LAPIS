namespace Wukna.Features.Users;

using Features.Board;
using Microsoft.AspNetCore.Identity;

public class User : IdentityUser<Guid>
{
    public User()
    {
        // Identity doesn't generate Guid keys until configured to do so, so users receive
        // their application identifier when the entity is constructed.
        Id = Guid.NewGuid();
        Username = $"user-{Id:N}"[..15];
        NormalizedUsername = Username.ToUpperInvariant();
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ProfileImageKey { get; set; }
    public string? ProfileImageVersion { get; set; }
    public string OnboardingStatus { get; set; } = "NotStarted";
    public int OnboardingVersion { get; set; }

    // EF populates the initialized collection; replacing the collection itself isn't required.
    public ICollection<BoardMembership> BoardMemberships { get; } = new List<BoardMembership>();
}
