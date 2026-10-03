namespace Wukna.Features.Chat;

using Microsoft.Extensions.Options;
using Wukna.Shared.Data.AppDbContext;

public sealed class ChatRevocation(IChatRealtimePublisher publisher, WuknaDbContext db,
    IOptions<ChatOutboxOptions> options, TimeProvider clock, ILogger<ChatRevocation> logger)
{
    public ChatOutboxEvent Record(Guid boardId, Guid? userId = null, Guid? instanceId = null)
    {
        var notification = new ChatOutboxEvent
        {
            BoardId = boardId, Kind = ChatOutboxEventKind.AccessRevoked,
            MemberUserId = userId, MembershipInstanceId = instanceId,
            CreatedAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow()
        };
        db.ChatOutboxEvents.Add(notification);
        return notification;
    }

    // Best effort immediate eviction after commit. The recorded outbox event
    // remains pending independently, so transport failure never loses recovery.
    public async Task NotifyAsync(ChatOutboxEvent notification)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.PublicationTimeoutSeconds));
        try { await publisher.PublishAsync(db, notification, deadline.Token); }
        catch (Exception exception)
        {
            logger.LogWarning("Immediate chat revocation will retry from outbox for event {EventId}: {FailureType}",
                notification.Id, exception.GetType().Name);
        }
    }
}
