namespace Wukna.Features.Chat;

public sealed record ChatSubscription(string ConnectionId, Guid UserId, Guid BoardId, Guid MembershipInstanceId);

// Transport subscriptions only. Database membership remains authoritative.
// Entries exist only for active connections and are removed on disconnect.
public sealed class ChatConnectionRegistry(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Connection> connections = [];

    public void Connect(string connectionId, Guid userId)
    {
        lock (gate) connections[connectionId] = new Connection(userId, clock.GetUtcNow());
    }

    public void Disconnect(string connectionId)
    {
        lock (gate) connections.Remove(connectionId);
    }

    public bool AllowInvocation(string connectionId)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(connectionId, out var connection)) return false;
            var now = clock.GetUtcNow();
            if (now >= connection.WindowStart.AddMinutes(1))
            {
                connection.WindowStart = now;
                connection.Invocations = 0;
            }
            return ++connection.Invocations <= 60;
        }
    }

    public bool Subscribe(ChatSubscription subscription)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(subscription.ConnectionId, out var connection) ||
                connection.UserId != subscription.UserId) return false;
            if (!connection.Boards.ContainsKey(subscription.BoardId) && connection.Boards.Count >= 8) return false;
            connection.Boards[subscription.BoardId] = subscription;
            return true;
        }
    }

    public ChatSubscription? Subscription(string connectionId, Guid boardId)
    {
        lock (gate) return connections.GetValueOrDefault(connectionId)?.Boards.GetValueOrDefault(boardId);
    }

    public long? NextTypingSequence(string connectionId)
    {
        lock (gate) return connections.TryGetValue(connectionId, out var connection) ? ++connection.TypingSequence : null;
    }

    public bool AllowTypingInvocation(string connectionId)
    {
        lock (gate)
        {
            if (!connections.TryGetValue(connectionId, out var connection)) return false;
            var now = clock.GetUtcNow();
            if (now >= connection.TypingWindow.AddMinutes(1))
            {
                connection.TypingWindow = now; connection.TypingInvocations = 0;
            }
            return ++connection.TypingInvocations <= 120;
        }
    }

    public void Remove(string connectionId, Guid boardId, Guid? instanceId = null)
    {
        lock (gate)
        {
            if (connections.TryGetValue(connectionId, out var connection) &&
                connection.Boards.TryGetValue(boardId, out var subscription) &&
                (instanceId is null || subscription.MembershipInstanceId == instanceId))
                connection.Boards.Remove(boardId);
        }
    }

    public IReadOnlyList<ChatSubscription> ForBoard(Guid boardId)
    {
        lock (gate) return connections.Values.Select(connection => connection.Boards.GetValueOrDefault(boardId))
            .OfType<ChatSubscription>().ToArray();
    }

    private sealed class Connection(Guid userId, DateTimeOffset now)
    {
        public Guid UserId { get; } = userId;
        public DateTimeOffset WindowStart { get; set; } = now;
        public int Invocations { get; set; }
        public long TypingSequence { get; set; }
        public DateTimeOffset TypingWindow { get; set; } = now;
        public int TypingInvocations { get; set; }
        public Dictionary<Guid, ChatSubscription> Boards { get; } = [];
    }
}
