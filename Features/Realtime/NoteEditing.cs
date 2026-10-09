namespace Wukna.Features.Realtime;

using Microsoft.AspNetCore.SignalR;

public sealed record StartNoteEditingRequest(
    Guid BoardId,
    Guid NoteId,
    long Sequence);

public sealed record StopNoteEditingRequest(
    Guid BoardId,
    Guid NoteId,
    long Sequence);

public sealed record NoteEditingStartedEvent(
    Guid BoardId,
    Guid NoteId,
    Guid UserId,
    string ConnectionId,
    long Sequence,
    DateTimeOffset ExpiresAt);

public sealed record NoteEditingStoppedEvent(
    Guid BoardId,
    Guid NoteId,
    Guid UserId,
    string ConnectionId,
    long Sequence);

public interface INoteEditingRegistry
{
    NoteEditingStartedEvent? Renew(NoteEditingStartedEvent state);
    NoteEditingStoppedEvent? Stop(
        Guid boardId,
        Guid noteId,
        string connectionId,
        long sequence,
        DateTimeOffset now);
    IReadOnlyList<NoteEditingStoppedEvent> EndBoard(
        Guid boardId,
        string connectionId);
    IReadOnlyList<NoteEditingStoppedEvent> EndConnection(string connectionId);
    IReadOnlyList<NoteEditingStoppedEvent> Expire(DateTimeOffset now);
}

public sealed class NoteEditingRegistry : INoteEditingRegistry
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);

    private readonly object gate = new();
    private readonly Dictionary<(string ConnectionId, Guid BoardId, Guid NoteId), Entry> states = [];

    public NoteEditingStartedEvent? Renew(NoteEditingStartedEvent state)
    {
        lock (gate)
        {
            var key = (state.ConnectionId, state.BoardId, state.NoteId);
            if (states.TryGetValue(key, out var current) &&
                state.Sequence <= current.Sequence)
                return null;
            states[key] = new Entry(
                state.UserId,
                state.Sequence,
                state.ExpiresAt,
                true);
            return state;
        }
    }

    public NoteEditingStoppedEvent? Stop(
        Guid boardId,
        Guid noteId,
        string connectionId,
        long sequence,
        DateTimeOffset now)
    {
        lock (gate)
        {
            var key = (connectionId, boardId, noteId);
            if (!states.TryGetValue(key, out var current) || sequence < current.Sequence)
                return null;
            states[key] = current with
            {
                Sequence = sequence,
                ExpiresAt = now + Lifetime,
                Active = false
            };
            return current.Active
                ? new NoteEditingStoppedEvent(
                    boardId, noteId, current.UserId, connectionId, sequence)
                : null;
        }
    }

    public IReadOnlyList<NoteEditingStoppedEvent> EndBoard(
        Guid boardId,
        string connectionId) =>
        EndWhere(key => key.BoardId == boardId && key.ConnectionId == connectionId);

    public IReadOnlyList<NoteEditingStoppedEvent> EndConnection(string connectionId) =>
        EndWhere(key => key.ConnectionId == connectionId);

    public IReadOnlyList<NoteEditingStoppedEvent> Expire(DateTimeOffset now)
    {
        lock (gate)
        {
            var expired = new List<NoteEditingStoppedEvent>();
            foreach (var pair in states.ToArray())
            {
                if (pair.Value.ExpiresAt > now) continue;
                if (!pair.Value.Active)
                {
                    states.Remove(pair.Key);
                    continue;
                }
                expired.Add(new NoteEditingStoppedEvent(
                    pair.Key.BoardId,
                    pair.Key.NoteId,
                    pair.Value.UserId,
                    pair.Key.ConnectionId,
                    pair.Value.Sequence));
                states[pair.Key] = pair.Value with
                {
                    Active = false,
                    ExpiresAt = now + Lifetime
                };
            }
            return expired;
        }
    }

    private IReadOnlyList<NoteEditingStoppedEvent> EndWhere(
        Func<(string ConnectionId, Guid BoardId, Guid NoteId), bool> predicate)
    {
        lock (gate)
        {
            var stopped = new List<NoteEditingStoppedEvent>();
            foreach (var pair in states.ToArray())
            {
                if (!predicate(pair.Key)) continue;
                states.Remove(pair.Key);
                if (pair.Value.Active)
                    stopped.Add(new NoteEditingStoppedEvent(
                        pair.Key.BoardId,
                        pair.Key.NoteId,
                        pair.Value.UserId,
                        pair.Key.ConnectionId,
                        pair.Value.Sequence));
            }
            return stopped;
        }
    }

    private sealed record Entry(
        Guid UserId,
        long Sequence,
        DateTimeOffset ExpiresAt,
        bool Active);
}

public sealed class NoteEditingCleanupService(
    INoteEditingRegistry editing,
    IBoardRealtimePublisher publisher,
    TimeProvider timeProvider,
    ILogger<NoteEditingCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var stopped in editing.Expire(timeProvider.GetUtcNow()))
                {
                    try
                    {
                        await publisher.PublishBoardAsync(stopped.BoardId,
                            BoardRealtimeEvents.NoteEditingStopped,
                            stopped,
                            stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(
                            exception,
                            "Expired editing state for board {BoardId} and note {NoteId} was not delivered",
                            stopped.BoardId,
                            stopped.NoteId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
