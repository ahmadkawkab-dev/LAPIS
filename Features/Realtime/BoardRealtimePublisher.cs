namespace Wukna.Features.Realtime;

using Microsoft.AspNetCore.SignalR;

public static class BoardRealtimeEvents
{
    public const string BoardUpdated = "BoardUpdated";
    public const string NoteCreated = "NoteCreated";
    public const string NoteUpdated = "NoteUpdated";
    public const string NoteDeleted = "NoteDeleted";
    public const string ConnectionCreated = "ConnectionCreated";
    public const string ConnectionUpdated = "ConnectionUpdated";
    public const string ConnectionDeleted = "ConnectionDeleted";
    public const string MembersChanged = "MembersChanged";
    public const string ProfileChanged = "ProfileChanged";
    public const string UserProfileChanged = "UserProfileChanged";
    public const string BoardSummaryChanged = "BoardSummaryChanged";
    public const string BoardSummaryRemoved = "BoardSummaryRemoved";
    public const string BoardAccessRevoked = "BoardAccessRevoked";
    public const string NoteGeometryPreview = "NoteGeometryPreview";
    public const string NoteGeometryPreviewEnded = "NoteGeometryPreviewEnded";
    public const string BoardPresenceChanged = "BoardPresenceChanged";
    public const string NoteEditingStarted = "NoteEditingStarted";
    public const string NoteEditingStopped = "NoteEditingStopped";
    public const string BoardCursorMoved = "BoardCursorMoved";
    public const string BoardCursorStopped = "BoardCursorStopped";
}

public sealed record BoardAccessRevokedEvent(Guid BoardId);

/// <summary>
/// Keeps application code independent of SignalR. Domain-specific event methods can be added as
/// current mutations are wired in the next phase.
/// </summary>
public interface IBoardRealtimePublisher
{
    Task PublishBoardAsync<TEvent>(
        Guid boardId,
        string eventName,
        TEvent message,
        CancellationToken cancellationToken = default);

    Task PublishUsersAsync<TEvent>(
        IReadOnlyCollection<Guid> userIds,
        string eventName,
        TEvent message,
        CancellationToken cancellationToken = default);

    Task PublishBoardExceptAsync<TEvent>(Guid boardId, string? excludedConnection, string eventName,
        TEvent message, CancellationToken cancellationToken = default);
    Task PublishBoardUsersAsync<TEvent>(Guid boardId, long membershipRevision, IReadOnlyCollection<Guid> userIds,
        string eventName, TEvent message, CancellationToken cancellationToken = default);

    Task RevokeBoardAccessAsync(
        Guid boardId,
        Guid userId,
        CancellationToken cancellationToken = default, BoardAccessChange? change = null);

    Task StopBoardEditingAsync(
        Guid boardId,
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed class BoardRealtimePublisher(
    IHubContext<BoardHub> hub,
    IBoardConnectionRegistry connections,
    INoteGeometryPreviewRegistry previews,
    INoteEditingRegistry editing,
    IBoardCursorRegistry cursors,
    ILogger<BoardRealtimePublisher> logger) : IBoardRealtimePublisher
{
    public Task PublishBoardAsync<TEvent>(Guid boardId, string eventName, TEvent message,
        CancellationToken cancellationToken = default) =>
        PublishBoardExceptAsync(boardId, null, eventName, message, cancellationToken);

    public Task PublishBoardExceptAsync<TEvent>(Guid boardId, string? excludedConnection,
        string eventName, TEvent message, CancellationToken cancellationToken = default) =>
        connections.StartPublication(boardId, excludedConnection, recipients =>
            hub.Clients.Clients(recipients.ToArray()).SendAsync(eventName, message, cancellationToken));

    public Task PublishBoardUsersAsync<TEvent>(Guid boardId, long membershipRevision,
        IReadOnlyCollection<Guid> userIds, string eventName, TEvent message,
        CancellationToken cancellationToken = default) =>
        connections.StartUserPublication(boardId, membershipRevision, () => userIds.Count == 0
            ? Task.CompletedTask
            : hub.Clients.Users(userIds.Select(id => id.ToString("D")).ToArray())
                .SendAsync(eventName, message, cancellationToken));

    public Task PublishUsersAsync<TEvent>(IReadOnlyCollection<Guid> userIds, string eventName,
        TEvent message, CancellationToken cancellationToken = default)
    {
        // Known sensitive board contracts require a board authorization boundary.
        // Preserve generic user addressing for existing personal/control messages.
        if (eventName is BoardRealtimeEvents.BoardUpdated or BoardRealtimeEvents.NoteCreated or
            BoardRealtimeEvents.NoteUpdated or BoardRealtimeEvents.NoteDeleted or BoardRealtimeEvents.ConnectionCreated or
            BoardRealtimeEvents.ConnectionUpdated or BoardRealtimeEvents.ConnectionDeleted or BoardRealtimeEvents.MembersChanged or
            BoardRealtimeEvents.ProfileChanged or BoardRealtimeEvents.BoardSummaryChanged or BoardRealtimeEvents.NoteGeometryPreview or
            BoardRealtimeEvents.NoteGeometryPreviewEnded or BoardRealtimeEvents.BoardPresenceChanged or BoardRealtimeEvents.NoteEditingStarted or
            BoardRealtimeEvents.NoteEditingStopped or BoardRealtimeEvents.BoardCursorMoved or BoardRealtimeEvents.BoardCursorStopped)
            throw new InvalidOperationException("Sensitive board user publications require validated recipients.");
        return userIds.Count == 0 ? Task.CompletedTask : hub.Clients.Users(userIds.Select(id => id.ToString("D")).ToArray())
            .SendAsync(eventName, message, cancellationToken);
    }

    public async Task StopBoardEditingAsync(Guid boardId, Guid userId, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in connections.GetConnections(boardId, userId))
        {
            foreach (var preview in previews.EndBoard(boardId, connectionId))
                await PublishBoardAsync(boardId, BoardRealtimeEvents.NoteGeometryPreviewEnded, BoardHub.Ended(preview), cancellationToken);
            foreach (var stopped in editing.EndBoard(boardId, connectionId))
                await PublishBoardAsync(boardId, BoardRealtimeEvents.NoteEditingStopped, stopped, cancellationToken);
        }
    }

    public async Task RevokeBoardAccessAsync(Guid boardId, Guid userId,
        CancellationToken cancellationToken = default, BoardAccessChange? change = null)
    {
        var connectionIds = change is null ? connections.RemoveUser(boardId, userId).ConnectionIds
            : change.Connections.GetValueOrDefault(userId) ?? [];
        // A re-invited member must explicitly rejoin. An obsolete revocation must
        // never remove or notify that new registration generation.
        var renewed = connections.GetConnections(boardId, userId);
        connectionIds = connectionIds.Where(id => !renewed.Contains(id)).ToArray();
        if (connectionIds.Count == 0) return;
        // Consume the old registrations' transient state before yielding. A fresh
        // join after lease release must never have its state cleared by old cleanup.
        var endedPreviews = connectionIds.SelectMany(id => previews.EndBoard(boardId, id)).ToArray();
        var endedEditing = connectionIds.SelectMany(id => editing.EndBoard(boardId, id)).ToArray();
        var endedCursors = connectionIds.SelectMany(id => cursors.EndBoard(boardId, id)).ToArray();
        // Initiate the intentional control while the endpoint still owns its
        // lifecycle lease. Its asynchronous completion and other cleanup do not
        // hold that lease or block new membership operations.
        var control = hub.Clients.Clients(connectionIds.ToArray()).SendAsync(BoardRealtimeEvents.BoardAccessRevoked,
            new BoardAccessRevokedEvent(boardId), cancellationToken);
        foreach (var preview in endedPreviews)
            await TryCleanupEventAsync(boardId, BoardRealtimeEvents.NoteGeometryPreviewEnded, BoardHub.Ended(preview), cancellationToken);
        foreach (var stopped in endedEditing)
            await TryCleanupEventAsync(boardId, BoardRealtimeEvents.NoteEditingStopped, stopped, cancellationToken);
        foreach (var stopped in endedCursors)
            await TryCleanupEventAsync(boardId, BoardRealtimeEvents.BoardCursorStopped, stopped, cancellationToken);
        // Group cleanup is queued by the registry and handled independently. Its
        // failure cannot prevent these intentional control notifications.
        await TryCleanupEventAsync(boardId, BoardRealtimeEvents.BoardPresenceChanged,
            connections.GetPresence(boardId), cancellationToken);
        await control;
    }

    private async Task TryCleanupEventAsync<TEvent>(Guid boardId, string eventName, TEvent message,
        CancellationToken ct)
    {
        try
        {
            await PublishBoardAsync(boardId, eventName, message, ct);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Realtime cleanup {EventName} was not delivered for board {BoardId}", eventName, boardId);
        }
    }
}
