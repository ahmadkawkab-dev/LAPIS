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

    Task RevokeBoardAccessAsync(
        Guid boardId,
        Guid userId,
        CancellationToken cancellationToken = default);

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
    public async Task StopBoardEditingAsync(
        Guid boardId, Guid userId, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in connections.GetConnections(boardId, userId))
        {
            foreach (var preview in previews.EndBoard(boardId, connectionId))
                await hub.Clients.Group(BoardRealtimeGroups.ForBoard(boardId)).SendAsync(
                    BoardRealtimeEvents.NoteGeometryPreviewEnded,
                    BoardHub.Ended(preview), cancellationToken);
            foreach (var stopped in editing.EndBoard(boardId, connectionId))
                await hub.Clients.Group(BoardRealtimeGroups.ForBoard(boardId)).SendAsync(
                    BoardRealtimeEvents.NoteEditingStopped, stopped, cancellationToken);
        }
    }
    public Task PublishBoardAsync<TEvent>(
        Guid boardId,
        string eventName,
        TEvent message,
        CancellationToken cancellationToken = default) =>
        hub.Clients.Group(BoardRealtimeGroups.ForBoard(boardId))
            .SendAsync(eventName, message, cancellationToken);

    public Task PublishUsersAsync<TEvent>(
        IReadOnlyCollection<Guid> userIds,
        string eventName,
        TEvent message,
        CancellationToken cancellationToken = default) =>
        userIds.Count == 0
            ? Task.CompletedTask
            : hub.Clients.Users(userIds.Select(userId => userId.ToString("D")))
                .SendAsync(eventName, message, cancellationToken);

    public async Task RevokeBoardAccessAsync(
        Guid boardId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var removal = connections.RemoveUser(boardId, userId);
        var connectionIds = removal.ConnectionIds;
        if (connectionIds.Count == 0) return;

        foreach (var connectionId in connectionIds)
        {
            foreach (var preview in previews.EndBoard(boardId, connectionId))
            {
                try
                {
                    await hub.Clients.GroupExcept(
                        BoardRealtimeGroups.ForBoard(boardId),
                        [connectionId]).SendAsync(
                            BoardRealtimeEvents.NoteGeometryPreviewEnded,
                            BoardHub.Ended(preview),
                            cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception,
                        "Geometry cleanup was not delivered during access revocation for board {BoardId}",
                        boardId);
                }
            }
        }

        foreach (var connectionId in connectionIds)
        {
            foreach (var stopped in editing.EndBoard(boardId, connectionId))
            {
                var recipients = connections.GetConnections(boardId);
                if (recipients.Count > 0)
                {
                    try
                    {
                        await hub.Clients.Clients(recipients).SendAsync(
                            BoardRealtimeEvents.NoteEditingStopped,
                            stopped,
                            cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(exception,
                            "Editing cleanup was not delivered during access revocation for board {BoardId}",
                            boardId);
                    }
                }
            }
        }

        foreach (var connectionId in connectionIds)
        {
            foreach (var stopped in cursors.EndBoard(boardId, connectionId))
            {
                var recipients = connections.GetConnections(boardId);
                if (recipients.Count == 0) continue;
                try
                {
                    await hub.Clients.Clients(recipients).SendAsync(
                        BoardRealtimeEvents.BoardCursorStopped,
                        stopped,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception,
                        "Cursor cleanup was not delivered during access revocation for board {BoardId}",
                        boardId);
                }
            }
        }

        await Task.WhenAll(connectionIds.Select(connectionId =>
            hub.Groups.RemoveFromGroupAsync(
                connectionId,
                BoardRealtimeGroups.ForBoard(boardId),
                cancellationToken)));

        var remainingConnectionIds = connections.GetConnections(boardId);
        if (remainingConnectionIds.Count > 0)
        {
            try
            {
                await hub.Clients.Clients(remainingConnectionIds).SendAsync(
                    BoardRealtimeEvents.BoardPresenceChanged,
                    removal.Presence.Snapshot,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception,
                    "Presence cleanup was not delivered during access revocation for board {BoardId}",
                    boardId);
            }
        }

        await hub.Clients.Clients(connectionIds).SendAsync(
            BoardRealtimeEvents.BoardAccessRevoked,
            new BoardAccessRevokedEvent(boardId),
            cancellationToken);
    }
}
