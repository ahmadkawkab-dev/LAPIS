namespace Wukna.Features.Chat;

using Wukna.Features.Board;

public sealed class BoardMemberChatState
{
    public Guid BoardId { get; set; }
    public Guid UserId { get; set; }
    public BoardMembership Membership { get; set; } = null!;
    // A fresh value on re-invitation prevents old connections inheriting new access.
    public Guid MembershipInstanceId { get; set; } = Guid.NewGuid();
    public bool IsMuted { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }
    public long ModerationRevision { get; set; }
    public DateTimeOffset? NextSendAllowedAt { get; set; }
    public long CooldownSettingsRevision { get; set; }
    public long LastReadSequence { get; set; }
}
