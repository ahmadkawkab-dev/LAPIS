namespace Wukna.Features.Realtime;

using System.Collections.Concurrent;

public sealed record BoardPresenceViewer(Guid UserId, int ConnectionCount);

public sealed record BoardPresenceSnapshot(
    Guid BoardId,
    long Revision,
    IReadOnlyList<BoardPresenceViewer> Viewers);

public sealed record BoardConnectionMutation(
    bool Changed,
    BoardPresenceSnapshot Snapshot);

public sealed record BoardUserConnectionRemoval(
    IReadOnlyCollection<string> ConnectionIds,
    BoardConnectionMutation Presence);

public interface IBoardConnectionRegistry
{
    BoardConnectionMutation Add(Guid boardId, Guid userId, string connectionId);
    BoardConnectionMutation Remove(Guid boardId, Guid userId, string connectionId);
    IReadOnlyList<BoardPresenceSnapshot> RemoveConnection(string connectionId);
    BoardUserConnectionRemoval RemoveUser(Guid boardId, Guid userId);
    bool Contains(Guid boardId, Guid userId, string connectionId);
    IReadOnlyCollection<string> GetConnections(Guid boardId, Guid userId);
    IReadOnlyCollection<string> GetConnections(Guid boardId);
    BoardPresenceSnapshot GetPresence(Guid boardId);
    Task<IAsyncDisposable> EnterLifecycleAsync(Guid boardId, CancellationToken ct = default);
    BoardAccessChange BeginAccessChange(Guid boardId, IReadOnlyCollection<Guid>? revokedUsers = null, bool deleteBoard = false);
    void CompleteAccessChange(BoardAccessChange change, bool committed);
    void ReconcileAccessChange(BoardAccessChange change, bool boardExists, IReadOnlySet<Guid> members);
    IReadOnlyList<BoardAccessChange> PendingAccessChanges(int limit, DateTimeOffset? due = null);
    bool IsAccessChangePending(BoardAccessChange change);
    void DeferReconciliation(BoardAccessChange change, DateTimeOffset now);
    long? PublicationRevision(Guid boardId);
    Task StartPublication(Guid boardId, string? excludedConnection, Func<IReadOnlyCollection<string>, Task> start);
    Task StartUserPublication(Guid boardId, long revision, Func<Task> start);
    IReadOnlyList<BoardGroupCleanup> CleanupCandidates(DateTimeOffset now, int limit, Guid? boardId = null);
    bool ClaimCleanup(BoardGroupCleanup cleanup);
    void FinishCleanup(BoardGroupCleanup cleanup, bool succeeded, DateTimeOffset now);
}

public sealed record BoardAccessChange(Guid BoardId, Guid Id, IReadOnlySet<Guid> RevokedUsers, bool DeleteBoard)
{
    public IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> Connections { get; init; } = new Dictionary<Guid, IReadOnlyCollection<string>>();
}
public sealed record BoardGroupCleanup(Guid BoardId, Guid UserId, string ConnectionId, long Generation);

public sealed class BoardConnectionRegistry : IBoardConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, BoardConnections> boards = new();

    public BoardConnectionMutation Add(Guid boardId, Guid userId, string connectionId) =>
        State(boardId).Add(userId, connectionId);

    public BoardConnectionMutation Remove(Guid boardId, Guid userId, string connectionId) =>
        State(boardId).Remove(userId, connectionId);

    public IReadOnlyList<BoardPresenceSnapshot> RemoveConnection(string connectionId)
    {
        var snapshots = new List<BoardPresenceSnapshot>();
        foreach (var state in boards.Values)
        {
            var mutation = state.RemoveConnection(connectionId);
            if (mutation.Changed) snapshots.Add(mutation.Snapshot);
        }
        return snapshots;
    }

    public BoardUserConnectionRemoval RemoveUser(Guid boardId, Guid userId) =>
        State(boardId).RemoveUser(userId);

    public bool Contains(Guid boardId, Guid userId, string connectionId) =>
        boards.TryGetValue(boardId, out var state) && state.Contains(userId, connectionId);

    public IReadOnlyCollection<string> GetConnections(Guid boardId, Guid userId) =>
        boards.TryGetValue(boardId, out var state) ? state.GetConnections(userId) : [];

    public IReadOnlyCollection<string> GetConnections(Guid boardId) =>
        boards.TryGetValue(boardId, out var state) ? state.GetConnections() : [];
    public BoardPresenceSnapshot GetPresence(Guid boardId) => State(boardId).CurrentPresence();

    // Single serving process only. Every membership writer and join must acquire this
    // lease before database locks. Publications use the short state lock, never this
    // asynchronous gate or a database transaction. Native groups do not authorize data.
    public async Task<IAsyncDisposable> EnterLifecycleAsync(Guid boardId, CancellationToken ct = default)
    {
        var state = State(boardId);
        await state.Lifecycle.WaitAsync(ct);
        return new LifecycleLease(state.Lifecycle);
    }
    public BoardAccessChange BeginAccessChange(Guid boardId, IReadOnlyCollection<Guid>? revokedUsers = null, bool deleteBoard = false) =>
        State(boardId).BeginAccessChange(new(boardId, Guid.NewGuid(), (revokedUsers ?? []).ToHashSet(), deleteBoard));
    public void CompleteAccessChange(BoardAccessChange change, bool committed) => State(change.BoardId).CompleteAccessChange(change, committed);
    public void ReconcileAccessChange(BoardAccessChange change, bool boardExists, IReadOnlySet<Guid> members) =>
        State(change.BoardId).ReconcileAccessChange(change, boardExists, members);
    public IReadOnlyList<BoardAccessChange> PendingAccessChanges(int limit, DateTimeOffset? due = null) =>
        boards.Values.Select(state => state.PendingChange(due)).OfType<BoardAccessChange>().Take(limit).ToArray();
    public bool IsAccessChangePending(BoardAccessChange change) => State(change.BoardId).PendingChange()?.Id == change.Id;
    public void DeferReconciliation(BoardAccessChange change, DateTimeOffset now) => State(change.BoardId).DeferReconciliation(change, now);
    public long? PublicationRevision(Guid boardId) => State(boardId).PublicationRevision();
    public Task StartPublication(Guid boardId, string? excludedConnection, Func<IReadOnlyCollection<string>, Task> start) =>
        State(boardId).StartPublication(excludedConnection, start);
    public Task StartUserPublication(Guid boardId, long revision, Func<Task> start) => State(boardId).StartUserPublication(revision, start);
    public IReadOnlyList<BoardGroupCleanup> CleanupCandidates(DateTimeOffset now, int limit, Guid? boardId = null) =>
        boards.Where(pair => boardId is null || pair.Key == boardId).SelectMany(pair => pair.Value.CleanupCandidates(now))
            .Take(limit).ToArray();
    public bool ClaimCleanup(BoardGroupCleanup cleanup) => State(cleanup.BoardId).ClaimCleanup(cleanup);
    public void FinishCleanup(BoardGroupCleanup cleanup, bool succeeded, DateTimeOffset now) => State(cleanup.BoardId).FinishCleanup(cleanup, succeeded, now);

    private sealed class LifecycleLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private BoardConnections State(Guid boardId) =>
        boards.GetOrAdd(boardId, static id => new BoardConnections(id));

    private sealed class BoardConnections(Guid boardId)
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, HashSet<string>> connections = new();
        private readonly Dictionary<string, long> generations = new();
        // At most one record per live connection/board pair, never an event queue.
        // Disconnection removes even exhausted records; a fresh authorized join replaces
        // old cleanup only after its outstanding native operation has actually settled.
        private readonly Dictionary<string, CleanupState> cleanup = new();
        private BoardAccessChange? pending;
        private int reconciliationAttempts;
        private DateTimeOffset reconcileAfter;
        private bool deleted;
        private long membershipRevision;
        private long generation;
        public SemaphoreSlim Lifecycle { get; } = new(1, 1);
        private long revision;

        private bool Eligible(Guid userId) => !deleted && pending?.DeleteBoard != true && pending?.RevokedUsers.Contains(userId) != true;

        public BoardConnectionMutation Add(Guid userId, string connectionId)
        {
            lock (gate)
            {
                if (deleted || pending is not null || cleanup.GetValueOrDefault(connectionId)?.InFlight == true)
                    throw new InvalidOperationException("Board registration is awaiting authorization reconciliation.");
                if (!connections.TryGetValue(userId, out var userConnections))
                {
                    userConnections = new HashSet<string>(StringComparer.Ordinal);
                    connections.Add(userId, userConnections);
                }
                var changed = userConnections.Add(connectionId);
                if (changed)
                {
                    generations[connectionId] = ++generation;
                    cleanup.Remove(connectionId);
                    revision++;
                }
                return new BoardConnectionMutation(changed, Snapshot());
            }
        }

        public BoardConnectionMutation Remove(Guid userId, string connectionId)
        {
            lock (gate)
            {
                var changed = connections.TryGetValue(userId, out var userConnections) &&
                    userConnections.Remove(connectionId);
                if (changed)
                {
                    QueueCleanup(userId, connectionId);
                    if (userConnections!.Count == 0) connections.Remove(userId);
                    revision++;
                }
                return new BoardConnectionMutation(changed, Snapshot());
            }
        }

        public BoardConnectionMutation RemoveConnection(string connectionId)
        {
            lock (gate)
            {
                cleanup.Remove(connectionId);
                generations.Remove(connectionId);
                if (pending is not null)
                    pending = pending with { Connections = pending.Connections.ToDictionary(pair => pair.Key,
                        pair => (IReadOnlyCollection<string>)pair.Value.Where(id => id != connectionId).ToArray()) };
                var changed = false;
                foreach (var userId in connections.Keys.ToArray())
                {
                    var userConnections = connections[userId];
                    if (!userConnections.Remove(connectionId)) continue;
                    changed = true;
                    if (userConnections.Count == 0) connections.Remove(userId);
                }
                if (changed) revision++;
                return new BoardConnectionMutation(changed, Snapshot());
            }
        }

        public BoardUserConnectionRemoval RemoveUser(Guid userId)
        {
            lock (gate)
            {
                if (!connections.Remove(userId, out var removed))
                    return new BoardUserConnectionRemoval(
                        [],
                        new BoardConnectionMutation(false, Snapshot()));
                revision++;
                foreach (var connectionId in removed) QueueCleanup(userId, connectionId);
                return new BoardUserConnectionRemoval(
                    removed.ToArray(),
                    new BoardConnectionMutation(true, Snapshot()));
            }
        }

        public bool Contains(Guid userId, string connectionId)
        {
            lock (gate)
                return Eligible(userId) && connections.TryGetValue(userId, out var userConnections) &&
                    userConnections.Contains(connectionId);
        }

        public IReadOnlyCollection<string> GetConnections(Guid userId)
        {
            lock (gate)
                return Eligible(userId) && connections.TryGetValue(userId, out var userConnections)
                    ? userConnections.ToArray()
                    : [];
        }

        public IReadOnlyCollection<string> GetConnections()
        {
            lock (gate)
                return connections.Where(pair => Eligible(pair.Key)).SelectMany(pair => pair.Value).ToArray();
        }
        public BoardPresenceSnapshot CurrentPresence() { lock (gate) return Snapshot(); }

        public BoardAccessChange BeginAccessChange(BoardAccessChange change)
        {
            lock (gate)
            {
                if (pending is not null) throw new InvalidOperationException("Board membership transaction outcome is unresolved.");
                pending = change with { Connections = connections.Where(pair => change.DeleteBoard || change.RevokedUsers.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<string>)pair.Value.ToArray()) };
                reconciliationAttempts = 0;
                reconcileAfter = DateTimeOffset.MinValue;
                membershipRevision++;
                return pending;
            }
        }
        public void CompleteAccessChange(BoardAccessChange change, bool committed)
        {
            lock (gate)
            {
                if (pending?.Id != change.Id) return;
                if (committed)
                {
                    foreach (var userId in connections.Keys.Where(user => change.DeleteBoard || change.RevokedUsers.Contains(user)).ToArray())
                        RemoveUser(userId);
                    deleted |= change.DeleteBoard;
                }
                pending = null;
                membershipRevision++;
            }
        }
        public void ReconcileAccessChange(BoardAccessChange change, bool boardExists, IReadOnlySet<Guid> members)
        {
            lock (gate)
            {
                if (pending?.Id != change.Id) return;
                foreach (var userId in connections.Keys.Where(user => !boardExists || !members.Contains(user)).ToArray())
                    RemoveUser(userId);
                deleted = !boardExists;
                pending = null;
                membershipRevision++;
            }
        }
        public BoardAccessChange? PendingChange(DateTimeOffset? due = null)
        {
            lock (gate) return due is null || due >= reconcileAfter ? pending : null;
        }
        public void DeferReconciliation(BoardAccessChange change, DateTimeOffset now)
        {
            lock (gate)
            {
                if (pending?.Id != change.Id) return;
                reconciliationAttempts = Math.Min(reconciliationAttempts + 1, 6);
                reconcileAfter = now.AddSeconds(Math.Min(30, Math.Pow(2, reconciliationAttempts - 1)));
            }
        }
        public long? PublicationRevision() { lock (gate) return pending is null && !deleted ? membershipRevision : null; }
        public Task StartPublication(string? excluded, Func<IReadOnlyCollection<string>, Task> start)
        {
            lock (gate)
            {
                var recipients = connections.Where(pair => Eligible(pair.Key)).SelectMany(pair => pair.Value)
                    .Where(id => id != excluded).ToArray();
                // Invoke only the transport initiation here. Await its returned task
                // outside this monitor. No cached client proxy may bypass this boundary.
                return recipients.Length == 0 ? Task.CompletedTask : start(recipients);
            }
        }
        public Task StartUserPublication(long expectedRevision, Func<Task> start)
        {
            lock (gate)
            {
                if (pending is not null || deleted || membershipRevision != expectedRevision)
                    throw new BoardRecipientsChangedException();
                return start();
            }
        }
        private void QueueCleanup(Guid userId, string id)
        {
            if (!generations.TryGetValue(id, out var version)) return;
            cleanup.TryAdd(id, new(new(boardId, userId, id, version)));
            generations.Remove(id);
        }
        public IReadOnlyList<BoardGroupCleanup> CleanupCandidates(DateTimeOffset now)
        {
            lock (gate) return cleanup.Values.Where(item => !item.InFlight && item.Attempts < 5 && item.NextAttempt <= now)
                .Select(item => item.Record).ToArray();
        }
        public bool ClaimCleanup(BoardGroupCleanup record)
        {
            lock (gate)
            {
                if (!cleanup.TryGetValue(record.ConnectionId, out var item) || item.Record != record || item.InFlight || item.Attempts >= 5)
                    return false;
                item.InFlight = true;
                return true;
            }
        }
        public void FinishCleanup(BoardGroupCleanup record, bool succeeded, DateTimeOffset now)
        {
            lock (gate)
            {
                if (!cleanup.TryGetValue(record.ConnectionId, out var item) || item.Record != record) return;
                if (succeeded) cleanup.Remove(record.ConnectionId);
                else
                {
                    item.InFlight = false;
                    item.Attempts++;
                    item.NextAttempt = now.AddSeconds(Math.Pow(2, item.Attempts - 1));
                }
            }
        }
        private sealed class CleanupState(BoardGroupCleanup record)
        {
            public BoardGroupCleanup Record { get; } = record;
            public bool InFlight { get; set; }
            public int Attempts { get; set; }
            public DateTimeOffset NextAttempt { get; set; }
        }

        // Called only while holding gate, so mutation, revision, and immutable aggregate are one
        // atomic registry operation.
        private BoardPresenceSnapshot Snapshot() => new(
            boardId,
            revision,
            connections
                .Where(entry => Eligible(entry.Key))
                .OrderBy(entry => entry.Key)
                .Select(entry => new BoardPresenceViewer(entry.Key, entry.Value.Count))
                .ToArray());
    }
}

public sealed class BoardRecipientsChangedException : Exception
{
    public BoardRecipientsChangedException() : base("Board membership changed while recipients were being validated.") { }
}
