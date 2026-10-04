namespace Wukna.Features.Chat;

using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed record ChatTypingEvent(int EventVersion, Guid BoardId, string ConnectionId, Guid UserId,
    Guid MembershipInstanceId, string Sequence, bool IsTyping, DateTimeOffset ExpiresAt, ChatSenderDto? Sender);

public sealed class ChatTypingRegistry(ChatConnectionRegistry connections, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);
    private readonly object gate = new();
    private readonly Dictionary<(string ConnectionId, Guid BoardId), ChatTypingEvent> leases = [];

    public ChatTypingEvent? Set(ChatSubscription subscription, bool active, ChatSenderDto? sender)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in leases.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) leases.Remove(expired);
            var key = (subscription.ConnectionId, subscription.BoardId);
            leases.TryGetValue(key, out var previous);
            if (!active && previous is null) return null;
            if (active && previous is not null && previous.ExpiresAt - Lifetime + TimeSpan.FromSeconds(2) > now) return null;
            var sequence = connections.NextTypingSequence(subscription.ConnectionId);
            if (sequence is null) return null;
            var item = new ChatTypingEvent(1, subscription.BoardId, subscription.ConnectionId, subscription.UserId,
                subscription.MembershipInstanceId, sequence.Value.ToString(CultureInfo.InvariantCulture), active,
                active ? now + Lifetime : now, sender);
            if (active) leases[key] = item; else leases.Remove(key);
            return item;
        }
    }

    public IReadOnlyList<ChatTypingEvent> End(string? connectionId = null, Guid? boardId = null,
        Guid? userId = null, Guid? instance = null)
    {
        lock (gate)
        {
            var ended = new List<ChatTypingEvent>();
            foreach (var pair in leases.Where(pair => (connectionId is null || pair.Key.ConnectionId == connectionId) &&
                (boardId is null || pair.Key.BoardId == boardId) && (userId is null || pair.Value.UserId == userId) &&
                (instance is null || pair.Value.MembershipInstanceId == instance)).ToArray())
            {
                leases.Remove(pair.Key);
                var sequence = connections.NextTypingSequence(pair.Key.ConnectionId) ?? long.Parse(pair.Value.Sequence, CultureInfo.InvariantCulture) + 1;
                ended.Add(pair.Value with { Sequence = sequence.ToString(CultureInfo.InvariantCulture), IsTyping = false,
                    ExpiresAt = clock.GetUtcNow(), Sender = null });
            }
            return ended;
        }
    }
}

public sealed class ChatTypingService(IHubContext<ChatHub> hub, ChatConnectionRegistry connections,
    ChatTypingRegistry typing, TimeProvider clock, ILogger<ChatTypingService> logger)
{
    public async Task SetAsync(WuknaDbContext db, string connectionId, Guid userId, Guid boardId, bool active, CancellationToken ct)
    {
        var subscription = connections.Subscription(connectionId, boardId);
        if (subscription is null || subscription.UserId != userId) throw new HubException("chat_forbidden");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await using var transaction = await db.Database.BeginTransactionAsync(deadline.Token);
        // Keep the same ordering as durable sends/moderation without taking an update lock.
        if ((await db.Boards.FromSqlInterpolated($"SELECT * FROM boards WHERE id = {boardId} FOR KEY SHARE")
            .AsNoTracking().ToListAsync(deadline.Token)).Count == 0) throw new HubException("chat_forbidden");
        await db.BoardChatSettings.FromSqlInterpolated($"SELECT * FROM board_chat_settings WHERE board_id = {boardId} FOR SHARE")
            .AsNoTracking().ToListAsync(deadline.Token);
        var recipients = await ChatRecipients.LockAsync(db, connections, boardId, deadline.Token);
        if (!recipients.States.TryGetValue(userId, out var state) || !recipients.Members.ContainsKey(userId) ||
            state.MembershipInstanceId != subscription.MembershipInstanceId) throw new HubException("chat_forbidden");
        if (active && ChatModeration.IsMuted(state, clock.GetUtcNow())) throw new HubException("chat_muted");
        var sender = active ? ChatSenderDto.From(await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId, deadline.Token)) : null;
        var item = typing.Set(subscription, active, sender);
        if (item is not null) await PublishAsync(item, recipients, deadline.Token);
        await transaction.CommitAsync(deadline.Token);
    }

    public async Task ClearAsync(WuknaDbContext db, Guid boardId, Guid? userId, Guid? instance, CancellationToken ct)
    {
        var ended = typing.End(boardId: boardId, userId: userId, instance: instance);
        if (ended.Count == 0) return;
        var recipients = await ChatRecipients.LockAsync(db, connections, boardId, ct);
        foreach (var item in ended) await PublishAsync(item, recipients, ct);
    }

    public async Task EndConnectionAsync(WuknaDbContext db, string connectionId, Guid? boardId = null)
    {
        var ended = typing.End(connectionId, boardId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        foreach (var group in ended.GroupBy(item => item.BoardId))
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(deadline.Token);
                var recipients = await ChatRecipients.LockAsync(db, connections, group.Key, deadline.Token);
                foreach (var item in group) await PublishAsync(item, recipients, deadline.Token);
                await transaction.CommitAsync(deadline.Token);
            }
            catch (Exception exception)
            {
                logger.LogDebug("Chat typing cleanup failed for board {BoardId}: {FailureType}; leases expire automatically",
                    group.Key, exception.GetType().Name);
            }
        }
    }

    private Task PublishAsync(ChatTypingEvent item, ChatRecipients recipients, CancellationToken ct) =>
        hub.Clients.Clients(recipients.Subscriptions.Select(subscription => subscription.ConnectionId).ToArray())
            .SendAsync(ChatRealtimeEvents.TypingChanged, item, ct);
}
