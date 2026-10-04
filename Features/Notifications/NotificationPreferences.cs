namespace Wukna.Features.Notifications;

using Wukna.Features.Board;

public enum ChatNotificationMode { AllActivity = 0, MentionsAndReplies = 1, Muted = 2 }

public sealed class NotificationPreference
{
    public Guid UserId { get; set; }
    public bool InAppEnabled { get; set; } = true;
    public bool PushEnabled { get; set; }
    public bool SoundsMuted { get; set; }
    public double SoundVolume { get; set; } = 0.5;
    public bool ChatNotificationsEnabled { get; set; } = true;
    public bool TaskReminderNotificationsEnabled { get; set; } = true;
    public bool ScheduledTaskReminderNotificationsEnabled { get; set; } = true;
    public bool SharedBoardNotificationsEnabled { get; set; } = true;
    public bool BoardInvitationNotificationsEnabled { get; set; } = true;
    public bool TaskActivityNotificationsEnabled { get; set; } = true;
    public bool ChatSoundEnabled { get; set; } = true;
    public bool TaskReminderSoundEnabled { get; set; } = true;
    public bool ScheduledTaskPostedSoundEnabled { get; set; } = true;
    public bool BoardInvitationSoundEnabled { get; set; } = true;
    public bool TaskCompletedSoundEnabled { get; set; } = true;
    public bool PrivatePreviewsEnabled { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BoardNotificationPreference
{
    public Guid BoardId { get; set; }
    public Guid UserId { get; set; }
    public BoardMembership Membership { get; set; } = null!;
    public ChatNotificationMode Mode { get; set; }
    public bool SoundsMuted { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
