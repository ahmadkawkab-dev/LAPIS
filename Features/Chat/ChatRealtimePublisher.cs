namespace Wukna.Features.Chat;

using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Board;
using Wukna.Shared.Data.AppDbContext;

public static class ChatRealtimeEvents
{
    public const string MessageCreated = "ChatMessageCreated";
    public const string AccessRevoked = "ChatAccessRevoked";
    public const string SettingsChanged = "ChatSettingsChanged";
    public const string MemberStateChanged = "ChatMemberStateChanged";
    public const string TypingChanged = "ChatTypingChanged";
    public const string AttachmentChanged = "ChatAttachmentChanged";
}
public sealed record ChatMessageCreatedEvent(Guid EventId, int EventVersion, Guid BoardId,
    Guid MessageId, string Sequence);
public sealed record ChatAccessRevokedEvent(Guid EventId, int EventVersion, Guid BoardId, Guid? MembershipInstanceId);
public sealed record ChatSettingsChangedEvent(Guid EventId, int EventVersion, Guid BoardId, string Revision);
public sealed record ChatMemberStateChangedEvent(Guid EventId, int EventVersion, Guid BoardId,
    Guid MemberUserId, Guid MembershipInstanceId, string Revision);
public sealed record ChatAttachmentChangedEvent(Guid EventId, int EventVersion, Guid BoardId, Guid MessageId, Guid AttachmentId);

public interface IChatRealtimePublisher
{
    Task PublishAsync(WuknaDbContext db, ChatOutboxEvent notification, CancellationToken ct);
}

public sealed class ChatRealtimePublisher(IHubContext<ChatHub> hub, ChatConnectionRegistry connections,
    ChatTypingService typing, TimeProvider clock, ILogger<ChatRealtimePublisher> logger) : IChatRealtimePublisher
{
    public async Task PublishAsync(WuknaDbContext db, ChatOutboxEvent notification, CancellationToken ct)
    {
        if (notification.EventVersion != 1) throw new InvalidOperationException("chat_event_unsupported");
        if (notification.Kind == ChatOutboxEventKind.AccessRevoked)
        {
            await typing.ClearAsync(db, notification.BoardId, notification.MemberUserId, notification.MembershipInstanceId, ct);
            // Retain matching subscriptions until successful delivery so a failed
            // revocation notification can retry. They cannot receive message events:
            // current database membership and instance are checked independently.
            var revoked = connections.ForBoard(notification.BoardId).Where(subscription =>
                notification.MemberUserId is null || subscription.UserId == notification.MemberUserId &&
                subscription.MembershipInstanceId == notification.MembershipInstanceId).ToArray();
            foreach (var subscription in revoked)
            {
                await hub.Groups.RemoveFromGroupAsync(subscription.ConnectionId, ChatHub.Group(notification.BoardId), ct);
                await hub.Clients.Client(subscription.ConnectionId).SendAsync(ChatRealtimeEvents.AccessRevoked,
                    new ChatAccessRevokedEvent(notification.Id, notification.EventVersion, notification.BoardId,
                        subscription.MembershipInstanceId), ct);
                connections.Remove(subscription.ConnectionId, notification.BoardId, subscription.MembershipInstanceId);
            }
            logger.LogInformation("Chat access revoked for board {BoardId}, user {UserId}, connections {Count}",
                notification.BoardId, notification.MemberUserId, revoked.Length);
            return;
        }
        if (notification.Kind == ChatOutboxEventKind.MessageCreated && !await db.ChatMessages.AnyAsync(message => message.BoardId == notification.BoardId &&
            message.Id == notification.MessageId && message.Sequence == notification.MessageSequence, ct)) return;
        var recipients = await ChatRecipients.LockAsync(db, connections, notification.BoardId, ct);
        var targets = recipients.Subscriptions.Select(subscription => subscription.ConnectionId).ToArray();
        switch (notification.Kind)
        {
            case ChatOutboxEventKind.MessageCreated:
                await hub.Clients.Clients(targets).SendAsync(ChatRealtimeEvents.MessageCreated,
                    new ChatMessageCreatedEvent(notification.Id, 1, notification.BoardId, notification.MessageId!.Value,
                        notification.MessageSequence!.Value.ToString(CultureInfo.InvariantCulture)), ct);
                break;
            case ChatOutboxEventKind.SettingsChanged:
                if (notification.Revision is null || !await db.BoardChatSettings.AsNoTracking().AnyAsync(settings =>
                    settings.BoardId == notification.BoardId && settings.SettingsRevision >= notification.Revision, ct)) return;
                await hub.Clients.Clients(targets).SendAsync(ChatRealtimeEvents.SettingsChanged,
                    new ChatSettingsChangedEvent(notification.Id, 1, notification.BoardId,
                        notification.Revision.Value.ToString(CultureInfo.InvariantCulture)), ct);
                break;
            case ChatOutboxEventKind.AttachmentChanged:
                if (notification.MessageId is not { } messageId || notification.AttachmentId is not { } attachmentId ||
                    !await db.ChatAttachments.AnyAsync(attachment => attachment.Id == attachmentId && attachment.MessageId == messageId &&
                        attachment.Message.BoardId == notification.BoardId, ct)) return;
                await hub.Clients.Clients(targets).SendAsync(ChatRealtimeEvents.AttachmentChanged,
                    new ChatAttachmentChangedEvent(notification.Id, 1, notification.BoardId, messageId, attachmentId), ct);
                break;
            case ChatOutboxEventKind.MemberStateChanged:
                if (notification.MemberUserId is not { } userId || notification.MembershipInstanceId is not { } instance ||
                    notification.Revision is null || !recipients.States.TryGetValue(userId, out var state) ||
                    state.MembershipInstanceId != instance || state.ModerationRevision < notification.Revision) return;
                if (ChatModeration.IsMuted(state, clock.GetUtcNow()))
                    await typing.ClearAsync(db, notification.BoardId, userId, instance, ct);
                var privateTargets = recipients.Subscriptions.Where(subscription => subscription.UserId == userId ||
                    recipients.Members[subscription.UserId].Role == BoardRole.Owner).Select(subscription => subscription.ConnectionId).ToArray();
                await hub.Clients.Clients(privateTargets).SendAsync(ChatRealtimeEvents.MemberStateChanged,
                    new ChatMemberStateChangedEvent(notification.Id, 1, notification.BoardId, userId, instance,
                        notification.Revision.Value.ToString(CultureInfo.InvariantCulture)), ct);
                break;
            default: throw new InvalidOperationException("chat_event_unsupported");
        }
    }
}
