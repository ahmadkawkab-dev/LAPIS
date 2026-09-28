namespace Wukna.Features.Realtime;

using System.IdentityModel.Tokens.Jwt;
using Wukna.Features.Board;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

[Authorize]
public sealed class BoardHub(
    BoardAccess boardAccess,
    IBoardConnectionRegistry connections,
    INoteGeometryPreviewRegistry previews,
    INoteEditingRegistry editing,
    IBoardCursorRegistry cursors,
    TimeProvider timeProvider,
    ILogger<BoardHub> logger) : Hub
{
    public const string Path = "/hubs/board";

    public async Task<BoardPresenceSnapshot> JoinBoard(Guid boardId)
    {
        var userId = CurrentUserId();
        if (!await boardAccess.CanAccessAsync(userId, boardId, Context.ConnectionAborted))
            throw new HubException("Forbidden");

        var presence = connections.Add(boardId, userId, Context.ConnectionId);
        try
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                BoardRealtimeGroups.ForBoard(boardId),
                Context.ConnectionAborted);

            // Close the race where membership is revoked between the first check and group add.
            if (!await boardAccess.CanAccessAsync(userId, boardId, Context.ConnectionAborted))
                throw new HubException("Forbidden");
        }
        catch
        {
            var rollback = connections.Remove(boardId, userId, Context.ConnectionId);
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                BoardRealtimeGroups.ForBoard(boardId),
                CancellationToken.None);
            if (rollback.Changed)
                await TryBroadcastPresenceAsync(rollback.Snapshot, CancellationToken.None);
            throw;
        }
        await TryBroadcastPresenceToOthersAsync(
            presence.Snapshot,
            Context.ConnectionAborted);
        return presence.Snapshot;
    }

    public async Task LeaveBoard(Guid boardId)
    {
        var userId = CurrentUserId();
        foreach (var preview in previews.EndBoard(boardId, Context.ConnectionId))
            await BroadcastEndedAsync(preview, CancellationToken.None);
        var stoppedEditing = editing.EndBoard(boardId, Context.ConnectionId);
        var stoppedCursors = cursors.EndBoard(boardId, Context.ConnectionId);
        var presence = connections.Remove(boardId, userId, Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            BoardRealtimeGroups.ForBoard(boardId),
            CancellationToken.None);
        foreach (var stopped in stoppedEditing)
            await TryBroadcastEditingStoppedAsync(stopped, CancellationToken.None);
        foreach (var stopped in stoppedCursors)
            await TryBroadcastCursorStoppedAsync(stopped, CancellationToken.None);
        if (presence.Changed)
            await TryBroadcastPresenceAsync(presence.Snapshot, CancellationToken.None);
    }

    public async Task MoveBoardCursor(MoveBoardCursorRequest request)
    {
        var userId = CurrentUserId();
        if (!connections.Contains(request.BoardId, userId, Context.ConnectionId))
            throw new HubException("Board subscription required");
        if (request.Sequence <= 0 || !ValidCoordinate(request.X) || !ValidCoordinate(request.Y))
            throw new HubException("Invalid cursor position");

        var state = cursors.Move(new BoardCursorMovedEvent(
            request.BoardId,
            userId,
            Context.ConnectionId,
            request.X,
            request.Y,
            request.Sequence,
            timeProvider.GetUtcNow() + BoardCursorRegistry.Lifetime));
        if (state is null) return;
        // Close the race where revocation removes the subscription after the first check.
        if (!connections.Contains(request.BoardId, userId, Context.ConnectionId))
        {
            cursors.Stop(
                request.BoardId,
                Context.ConnectionId,
                request.Sequence,
                timeProvider.GetUtcNow());
            return;
        }
        await TryBroadcastCursorMovedAsync(state, Context.ConnectionAborted);
    }

    public async Task StopBoardCursor(StopBoardCursorRequest request)
    {
        var userId = CurrentUserId();
        if (!connections.Contains(request.BoardId, userId, Context.ConnectionId))
            throw new HubException("Board subscription required");
        if (request.Sequence <= 0) throw new HubException("Invalid cursor sequence");
        var stopped = cursors.Stop(
            request.BoardId,
            Context.ConnectionId,
            request.Sequence,
            timeProvider.GetUtcNow());
        if (stopped is not null)
            await TryBroadcastCursorStoppedAsync(stopped, Context.ConnectionAborted);
    }

    public async Task StartNoteEditing(StartNoteEditingRequest request)
    {
        var userId = CurrentUserId();
        if (!connections.Contains(request.BoardId, userId, Context.ConnectionId))
            throw new HubException("Board subscription required");
        if (request.Sequence <= 0) throw new HubException("Invalid editing sequence");
        if (!await boardAccess.CanEditTopLevelNoteAsync(
                userId,
                request.BoardId,
                request.NoteId,
                Context.ConnectionAborted))
            throw new HubException("Forbidden");

        var state = editing.Renew(new NoteEditingStartedEvent(
            request.BoardId,
            request.NoteId,
            userId,
            Context.ConnectionId,
            request.Sequence,
            timeProvider.GetUtcNow() + NoteEditingRegistry.Lifetime));
        if (state is not null)
            await TryBroadcastEditingStartedAsync(state, Context.ConnectionAborted);
    }

    public async Task StopNoteEditing(StopNoteEditingRequest request)
    {
        var userId = CurrentUserId();
        if (!connections.Contains(request.BoardId, userId, Context.ConnectionId))
            throw new HubException("Board subscription required");
        if (request.Sequence <= 0) throw new HubException("Invalid editing sequence");
        var stopped = editing.Stop(
            request.BoardId,
            request.NoteId,
            Context.ConnectionId,
            request.Sequence,
            timeProvider.GetUtcNow());
        if (stopped is not null)
            await TryBroadcastEditingStoppedAsync(stopped, Context.ConnectionAborted);
    }

    public async Task PreviewNoteGeometry(NoteGeometryPreviewRequest request)
    {
        var userId = CurrentUserId();
        if (!connections.Contains(request.BoardId, userId, Context.ConnectionId))
            throw new HubException("Board subscription required");
        if (!Valid(request)) throw new HubException("Invalid geometry preview");
        if (!await boardAccess.CanEditTopLevelNoteAsync(
                userId,
                request.BoardId,
                request.NoteId,
                Context.ConnectionAborted))
            throw new HubException("Forbidden");

        var active = new ActiveNoteGeometryPreview(
            request.BoardId,
            request.NoteId,
            userId,
            Context.ConnectionId,
            request.Sequence);
        if (!previews.TryAdvance(active)) return;

        var message = new NoteGeometryPreviewEvent(
            request.BoardId,
            request.NoteId,
            userId,
            Context.ConnectionId,
            request.Operation,
            request.X,
            request.Y,
            request.Width,
            request.Height,
            request.BaseVersion,
            request.Sequence,
            timeProvider.GetUtcNow());
        await Clients.OthersInGroup(BoardRealtimeGroups.ForBoard(request.BoardId)).SendAsync(
            BoardRealtimeEvents.NoteGeometryPreview,
            message,
            Context.ConnectionAborted);
    }

    public async Task EndNoteGeometryPreview(EndNoteGeometryPreviewRequest request)
    {
        var userId = CurrentUserId();
        if (!connections.Contains(request.BoardId, userId, Context.ConnectionId))
            throw new HubException("Board subscription required");
        var preview = previews.End(
            request.BoardId,
            request.NoteId,
            Context.ConnectionId,
            request.Sequence);
        if (preview is not null)
            await BroadcastEndedAsync(preview, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var ended = previews.EndConnection(Context.ConnectionId);
        var stoppedEditing = editing.EndConnection(Context.ConnectionId);
        var stoppedCursors = cursors.EndConnection(Context.ConnectionId);
        var presenceSnapshots = connections.RemoveConnection(Context.ConnectionId);
        var recipients = ended
            .Select(preview => preview.BoardId)
            .Distinct()
            .ToDictionary(
                boardId => boardId,
                boardId => connections.GetConnections(boardId)
                    .Where(connectionId => connectionId != Context.ConnectionId)
                    .ToArray());
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        foreach (var boardPreviews in ended.GroupBy(preview => preview.BoardId))
        {
            try
            {
                var connectionIds = recipients[boardPreviews.Key];
                if (connectionIds.Length == 0) continue;
                foreach (var preview in boardPreviews)
                {
                    await Clients.Clients(connectionIds).SendAsync(
                        BoardRealtimeEvents.NoteGeometryPreviewEnded,
                        Ended(preview),
                        cleanupTimeout.Token);
                }
            }
            catch (Exception cleanupException)
            {
                logger.LogDebug(
                    cleanupException,
                    "Realtime geometry disconnect cleanup fell back to client expiry for board {BoardId} and connection {ConnectionId}",
                    boardPreviews.Key,
                    Context.ConnectionId);
            }
        }
        foreach (var snapshot in presenceSnapshots)
            await TryBroadcastPresenceAsync(snapshot, cleanupTimeout.Token);
        foreach (var stopped in stoppedEditing)
            await TryBroadcastEditingStoppedAsync(stopped, cleanupTimeout.Token);
        foreach (var stopped in stoppedCursors)
            await TryBroadcastCursorStoppedAsync(stopped, cleanupTimeout.Token);
        await base.OnDisconnectedAsync(exception);
    }

    public static NoteGeometryPreviewEndedEvent Ended(ActiveNoteGeometryPreview preview) => new(
        preview.BoardId,
        preview.NoteId,
        preview.UserId,
        preview.ConnectionId,
        preview.Sequence);

    private Task BroadcastEndedAsync(
        ActiveNoteGeometryPreview preview,
        CancellationToken cancellationToken) =>
        Clients.OthersInGroup(BoardRealtimeGroups.ForBoard(preview.BoardId)).SendAsync(
            BoardRealtimeEvents.NoteGeometryPreviewEnded,
            Ended(preview),
            cancellationToken);

    private Task TryBroadcastPresenceToOthersAsync(
        BoardPresenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var connectionIds = connections.GetConnections(snapshot.BoardId)
            .Where(connectionId => connectionId != Context.ConnectionId)
            .ToArray();
        return connectionIds.Length == 0
            ? Task.CompletedTask
            : TrySendPresenceAsync(Clients.Clients(connectionIds), snapshot, cancellationToken);
    }

    private Task TryBroadcastPresenceAsync(
        BoardPresenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var connectionIds = connections.GetConnections(snapshot.BoardId);
        return connectionIds.Count == 0
            ? Task.CompletedTask
            : TrySendPresenceAsync(Clients.Clients(connectionIds), snapshot, cancellationToken);
    }

    private async Task TrySendPresenceAsync(
        IClientProxy clients,
        BoardPresenceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await clients.SendAsync(
                BoardRealtimeEvents.BoardPresenceChanged,
                snapshot,
                cancellationToken);
        }
        catch (Exception presenceException)
        {
            logger.LogDebug(
                presenceException,
                "Presence snapshot {Revision} for board {BoardId} was not delivered",
                snapshot.Revision,
                snapshot.BoardId);
        }
    }

    private Task TryBroadcastEditingStartedAsync(
        NoteEditingStartedEvent state,
        CancellationToken cancellationToken) =>
        TryBroadcastEditingAsync(
            state.BoardId,
            BoardRealtimeEvents.NoteEditingStarted,
            state,
            cancellationToken);

    private Task TryBroadcastEditingStoppedAsync(
        NoteEditingStoppedEvent state,
        CancellationToken cancellationToken) =>
        TryBroadcastEditingAsync(
            state.BoardId,
            BoardRealtimeEvents.NoteEditingStopped,
            state,
            cancellationToken);

    private Task TryBroadcastCursorMovedAsync(
        BoardCursorMovedEvent state,
        CancellationToken cancellationToken) =>
        TryBroadcastCursorAsync(
            state.BoardId,
            BoardRealtimeEvents.BoardCursorMoved,
            state,
            cancellationToken);

    private Task TryBroadcastCursorStoppedAsync(
        BoardCursorStoppedEvent state,
        CancellationToken cancellationToken) =>
        TryBroadcastCursorAsync(
            state.BoardId,
            BoardRealtimeEvents.BoardCursorStopped,
            state,
            cancellationToken);

    private async Task TryBroadcastEditingAsync<TEvent>(
        Guid boardId,
        string eventName,
        TEvent message,
        CancellationToken cancellationToken)
    {
        var connectionIds = connections.GetConnections(boardId)
            .Where(connectionId => connectionId != Context.ConnectionId)
            .ToArray();
        if (connectionIds.Length == 0) return;
        try
        {
            await Clients.Clients(connectionIds).SendAsync(
                eventName,
                message,
                cancellationToken);
        }
        catch (Exception editingException)
        {
            logger.LogDebug(
                editingException,
                "Editing event {EventName} for board {BoardId} was not delivered",
                eventName,
                boardId);
        }
    }

    private async Task TryBroadcastCursorAsync<TEvent>(
        Guid boardId,
        string eventName,
        TEvent message,
        CancellationToken cancellationToken)
    {
        var connectionIds = connections.GetConnections(boardId)
            .Where(connectionId => connectionId != Context.ConnectionId)
            .ToArray();
        if (connectionIds.Length == 0) return;
        try
        {
            await Clients.Clients(connectionIds).SendAsync(
                eventName,
                message,
                cancellationToken);
        }
        catch (Exception cursorException)
        {
            logger.LogDebug(
                cursorException,
                "Cursor event {EventName} for board {BoardId} was not delivered",
                eventName,
                boardId);
        }
    }

    private static bool Valid(NoteGeometryPreviewRequest request) =>
        request.Sequence > 0 && request.Operation switch
        {
            NoteGeometryOperation.Drag =>
                ValidFinite(request.X) &&
                ValidFinite(request.Y) &&
                request.Width is null &&
                request.Height is null,
            NoteGeometryOperation.Resize =>
                request.X is null &&
                request.Y is null &&
                ValidPositive(request.Width) &&
                ValidPositive(request.Height),
            _ => false
        };

    private static bool ValidFinite(double? value) =>
        value is double number && double.IsFinite(number);

    private static bool ValidPositive(double? value) =>
        value is double number && double.IsFinite(number) && number > 0;

    private static bool ValidCoordinate(double value) =>
        double.IsFinite(value);

    private Guid CurrentUserId() =>
        Guid.TryParse(Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId)
            ? userId
            : throw new HubException("Unauthenticated");
}

public static class BoardRealtimeGroups
{
    public static string ForBoard(Guid boardId) => $"board:{boardId:D}";
}
