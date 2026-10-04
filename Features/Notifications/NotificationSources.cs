namespace Wukna.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Wukna.Features.Chat;
using Wukna.Shared.Data.AppDbContext;

public static class NotificationSources
{
    public static Task ChatAsync(WuknaDbContext db, ChatMessage message, DateTimeOffset now, CancellationToken ct) =>
        BoardAsync(db, message.BoardId, message.SenderUserId, NotificationType.ChatActivity,
            message.Type == ChatMessageType.ScheduledTask ? "scheduledTaskPosted" : "message",
            "chatMessage", message.Id, $"chat:{message.Id:N}", now, ct, message.Sequence);

    // Must execute in the domain mutation's transaction, before SaveChanges/Commit.
    public static async Task BoardAsync(WuknaDbContext db, Guid boardId, Guid actor, NotificationType type,
        string activity, string resourceKind, Guid resourceId, string sourceKey, DateTimeOffset now,
        CancellationToken ct, long? sequence = null, Guid? onlyRecipient = null)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Notification sources require a domain transaction.");
        // Capture offline recipients as well as connected members. A new membership gets a fresh incarnation.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH recipients AS (
              SELECT board_id, user_id FROM board_memberships
              WHERE board_id = {boardId} AND user_id <> {actor} AND ({onlyRecipient}::uuid IS NULL OR user_id = {onlyRecipient})
              ORDER BY user_id FOR KEY SHARE
            )
            INSERT INTO board_member_chat_states
              (board_id, user_id, membership_instance_id, is_muted, moderation_revision,
               cooldown_settings_revision, last_read_sequence)
            SELECT board_id, user_id, gen_random_uuid(), FALSE, 0, 0, 0 FROM recipients
            ON CONFLICT (board_id, user_id) DO NOTHING
            """, ct);
        var recipients = await db.BoardMemberChatStates.AsNoTracking().Where(state => state.BoardId == boardId &&
            state.UserId != actor && (onlyRecipient == null || state.UserId == onlyRecipient))
            .Select(state => new { state.UserId, state.MembershipInstanceId }).ToArrayAsync(ct);
        foreach (var recipient in recipients)
            db.NotificationWork.Add(new NotificationWork
            {
                UserId = recipient.UserId, SourceEventKey = sourceKey, Type = type, ActivityKind = activity,
                ActorUserId = actor, BoardId = boardId, MembershipInstanceId = recipient.MembershipInstanceId,
                ResourceKind = resourceKind, ResourceId = resourceId, MessageSequence = sequence,
                CreatedAt = now, NextAttemptAt = sequence is null ? now : now.AddSeconds(2), ExpiresAt = now.AddHours(24)
            });
    }

    public static void Deliver(WuknaDbContext db, Notification item, DateTimeOffset now) =>
        db.NotificationWork.Add(new NotificationWork { Kind = NotificationWorkKind.SignalR,
            UserId = item.UserId, SourceEventKey = $"notification:{item.Id:N}:{item.Revision}",
            NotificationId = item.Id, NotificationRevision = item.Revision,
            CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddHours(24) });

    public static void StateChanged(WuknaDbContext db, Guid userId, DateTimeOffset now, bool preferences = false) =>
        db.NotificationWork.Add(new NotificationWork { Kind = preferences ? NotificationWorkKind.PreferencesChanged : NotificationWorkKind.StateChanged,
            UserId = userId, SourceEventKey = $"state:{Guid.NewGuid():N}",
            CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddHours(24) });
}
