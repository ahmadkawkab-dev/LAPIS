namespace Wukna.Features.Profile;

public sealed record UpdateProfileRequest(string? Username, string? DisplayName);
public sealed record ProfileDto(
    Guid UserId,
    string Username,
    string? DisplayName,
    string Email,
    string? ProfileImageUrl,
    string? ProfileImageVersion);
public sealed record AvatarDto(string? ProfileImageUrl, string? ProfileImageVersion);
public sealed record OnboardingDto(string Status, int Version);
