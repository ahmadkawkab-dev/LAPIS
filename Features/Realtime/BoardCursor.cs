namespace Wukna.Features.Realtime;

using Microsoft.AspNetCore.SignalR;

public sealed record MoveBoardCursorRequest(
    Guid BoardId,
    double X,
    double Y,
    long Sequence);

public sealed record StopBoardCursorRequest(
    Guid BoardId,
    long Sequence);

public sealed record BoardCursorMovedEvent(
    Guid BoardId,
    Guid UserId,
    string ConnectionId,
    double X,
    double Y,
    long Sequence,
    DateTimeOffset ExpiresAt);

public sealed record BoardCursorStoppedEvent(
    Guid BoardId,
    Guid UserId,
    string ConnectionId,
    long Sequence);

public interface IBoardCursorRegistry
{
    BoardCursorMovedEvent? Move(BoardCursorMovedEvent state);
    BoardCursorStoppedEvent? Stop(
        Guid boardId,
        string connectionId,
        long sequence,
        DateTimeOffset now);
    IReadOnlyList<BoardCursorStoppedEvent> EndBoard(Guid boardId, string connectionId);
    IReadOnlyList<BoardCursorStoppedEvent> EndConnection(string connectionId);
    IReadOnlyList<BoardCursorStoppedEvent> Expire(DateTimeOffset now);
}

public sealed class BoardCursorRegistry : IBoardCursorRegistry
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(1500);

    private readonly object gate = new();
    private readonly Dictionary<(string ConnectionId, Guid BoardId), Entry> states = [];

    public BoardCursorMovedEvent? Move(BoardCursorMovedEvent state)
    {
        lock (gate)
        {
            var key = (state.ConnectionId, state.BoardId);
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

    public BoardCursorStoppedEvent? Stop(
        Guid boardId,
        string connectionId,
        long sequence,
        DateTimeOffset now)
    {
        lock (gate)
        {
            var key = (connectionId, boardId);
            if (!states.TryGetValue(key, out var current) || sequence < current.Sequence)
                return null;
            states[key] = current with
            {
                Sequence = sequence,
                ExpiresAt = now + Lifetime,
                Active = false
            };
            return current.Active
                ? new BoardCursorStoppedEvent(
                    boardId, current.UserId, connectionId, sequence)
                : null;
        }
    }

    public IReadOnlyList<BoardCursorStoppedEvent> EndBoard(
        Guid boardId,
        string connectionId) =>
        EndWhere(key => key.BoardId == boardId && key.ConnectionId == connectionId);

    public IReadOnlyList<BoardCursorStoppedEvent> EndConnection(string connectionId) =>
        EndWhere(key => key.ConnectionId == connectionId);

    public IReadOnlyList<BoardCursorStoppedEvent> Expire(DateTimeOffset now)
    {
        lock (gate)
        {
            var expired = new List<BoardCursorStoppedEvent>();
            foreach (var pair in states.ToArray())
            {
                if (pair.Value.ExpiresAt > now) continue;
                if (!pair.Value.Active)
                {
                    states.Remove(pair.Key);
                    continue;
                }
                expired.Add(new BoardCursorStoppedEvent(
                    pair.Key.BoardId,
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

    private IReadOnlyList<BoardCursorStoppedEvent> EndWhere(
        Func<(string ConnectionId, Guid BoardId), bool> predicate)
    {
        lock (gate)
        {
            var stopped = new List<BoardCursorStoppedEvent>();
            foreach (var pair in states.ToArray())
            {
                if (!predicate(pair.Key)) continue;
                states.Remove(pair.Key);
                if (pair.Value.Active)
                    stopped.Add(new BoardCursorStoppedEvent(
                        pair.Key.BoardId,
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

public sealed class BoardCursorCleanupService(
    IBoardCursorRegistry cursors,
    IBoardRealtimePublisher publisher,
    TimeProvider timeProvider,
    ILogger<BoardCursorCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var stopped in cursors.Expire(timeProvider.GetUtcNow()))
                {
                    try
                    {
                        await publisher.PublishBoardExceptAsync(stopped.BoardId, stopped.ConnectionId,
                            BoardRealtimeEvents.BoardCursorStopped,
                            stopped,
                            stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(
                            exception,
                            "Expired cursor for board {BoardId} and connection {ConnectionId} was not delivered",
                            stopped.BoardId,
                            stopped.ConnectionId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
