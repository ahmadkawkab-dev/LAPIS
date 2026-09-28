namespace Wukna.Features.Realtime;

using Wukna.Features.Board;
using Wukna.Features.NoteConnection;
using Wukna.Features.Notes;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.EntityFrameworkCore;

public sealed record BoardUpdatedEvent(
    Guid BoardId,
    string Title,
    DateTimeOffset UpdatedAt);

public sealed record NoteDeletedEvent(Guid BoardId, Guid NoteId, uint Version);

public sealed record ConnectionDeletedEvent(Guid BoardId, Guid ConnectionId, uint Version = 0);

public sealed record MembersChangedEvent(Guid BoardId);
public sealed record ProfileChangedEvent(Guid BoardId, Guid UserId);
public sealed record UserProfileChangedEvent(Guid UserId);

public sealed record BoardSummaryRemovedEvent(Guid BoardId);

/// <summary>
/// Observes every post-commit publication attempt. Failures are logged but never reinterpret an
/// already committed REST command as failed. PostgreSQL remains the recovery source of truth.
/// </summary>
public sealed class BoardRealtimeDispatcher(
    IBoardRealtimePublisher publisher,
    BoardSummaryReader summaries,
    WuknaDbContext db,
    ILogger<BoardRealtimeDispatcher> logger)
{
    public Task BoardCreatedAsync(Guid boardId) => PublishSummariesAsync(boardId);

    public async Task ProfileChangedAsync(Guid userId)
    {
        await TryPublishAsync(BoardRealtimeEvents.UserProfileChanged, Guid.Empty, null, userId, null,
            () => publisher.PublishUsersAsync([userId], BoardRealtimeEvents.UserProfileChanged,
                new UserProfileChangedEvent(userId)));
        try
        {
            var boardIds = await db.BoardMemberships.AsNoTracking().Where(member => member.UserId == userId)
                .Select(member => member.BoardId).ToArrayAsync();
            foreach (var boardId in boardIds)
                await PublishBoardAsync(boardId, BoardRealtimeEvents.ProfileChanged,
                    new ProfileChangedEvent(boardId, userId), null, null);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not notify board members about profile changes for user {UserId}.", userId);
        }
    }

    public async Task BoardDeletedAsync(Guid boardId, IReadOnlyCollection<Guid> memberIds)
    {
        foreach (var memberId in memberIds)
        {
            await TryPublishAsync(
                BoardRealtimeEvents.BoardAccessRevoked, boardId, entityId: null,
                userId: memberId, version: null,
                () => publisher.RevokeBoardAccessAsync(boardId, memberId));
        }
        await TryPublishAsync(
            BoardRealtimeEvents.BoardSummaryRemoved, boardId, entityId: null,
            userId: null, version: null,
            () => publisher.PublishUsersAsync(memberIds,
                BoardRealtimeEvents.BoardSummaryRemoved,
                new BoardSummaryRemovedEvent(boardId)));
    }

    public async Task BoardUpdatedAsync(BoardUpdatedEvent message)
    {
        await PublishBoardAsync(
            message.BoardId,
            BoardRealtimeEvents.BoardUpdated,
            message,
            entityId: null,
            version: null);
        await PublishSummariesAsync(message.BoardId);
    }

    public async Task NoteCreatedAsync(NoteDto note)
    {
        await PublishBoardAsync(
            note.BoardId,
            BoardRealtimeEvents.NoteCreated,
            note,
            note.Id,
            note.Version);
        await PublishSummariesAsync(note.BoardId);
    }

    public async Task NoteUpdatedAsync(NoteDto note)
    {
        await PublishBoardAsync(
            note.BoardId,
            BoardRealtimeEvents.NoteUpdated,
            note,
            note.Id,
            note.Version);
        await PublishSummariesAsync(note.BoardId);
    }

    public async Task NoteDeletedAsync(NoteDeletedEvent message)
    {
        await PublishBoardAsync(
            message.BoardId,
            BoardRealtimeEvents.NoteDeleted,
            message,
            message.NoteId,
            message.Version);
        await PublishSummariesAsync(message.BoardId);
    }

    public async Task ConnectionCreatedAsync(NoteConnectionDto connection)
    {
        await PublishBoardAsync(
            connection.BoardId,
            BoardRealtimeEvents.ConnectionCreated,
            connection,
            connection.Id,
            version: null);
        await PublishSummariesAsync(connection.BoardId);
    }

    public async Task ConnectionUpdatedAsync(NoteConnectionDto connection)
    {
        await PublishBoardAsync(connection.BoardId, BoardRealtimeEvents.ConnectionUpdated,
            connection, connection.Id, connection.Version);
        await PublishSummariesAsync(connection.BoardId);
    }

    public async Task ConnectionDeletedAsync(ConnectionDeletedEvent message)
    {
        await PublishBoardAsync(
            message.BoardId,
            BoardRealtimeEvents.ConnectionDeleted,
            message,
            message.ConnectionId,
            version: null);
        await PublishSummariesAsync(message.BoardId);
    }

    public async Task MembersChangedAsync(
        Guid boardId, Guid? removedUserId = null, Guid? downgradedUserId = null)
    {
        if (downgradedUserId is Guid downgraded)
            await TryPublishAsync(
                BoardRealtimeEvents.MembersChanged, boardId,
                entityId: null, userId: downgraded, version: null,
                () => publisher.StopBoardEditingAsync(boardId, downgraded));
        if (removedUserId is Guid removed)
        {
            await TryPublishAsync(
                BoardRealtimeEvents.BoardAccessRevoked,
                boardId,
                entityId: null,
                removed,
                version: null,
                () => publisher.RevokeBoardAccessAsync(boardId, removed));
            await TryPublishAsync(
                BoardRealtimeEvents.BoardSummaryRemoved,
                boardId,
                entityId: null,
                removed,
                version: null,
                () => publisher.PublishUsersAsync(
                    [removed],
                    BoardRealtimeEvents.BoardSummaryRemoved,
                    new BoardSummaryRemovedEvent(boardId)));
        }

        var recipients = await ReadSummariesAsync(boardId);
        if (recipients is null) return;

        await TryPublishAsync(
            BoardRealtimeEvents.MembersChanged,
            boardId,
            entityId: null,
            userId: null,
            version: null,
            () => publisher.PublishUsersAsync(
                recipients.Select(recipient => recipient.UserId).ToArray(),
                BoardRealtimeEvents.MembersChanged,
                new MembersChangedEvent(boardId)));
        await PublishSummariesAsync(boardId, recipients);
    }

    private Task PublishBoardAsync<TEvent>(
        Guid boardId,
        string eventName,
        TEvent message,
        Guid? entityId,
        uint? version) =>
        TryPublishAsync(
            eventName,
            boardId,
            entityId,
            userId: null,
            version,
            () => publisher.PublishBoardAsync(boardId, eventName, message));

    private async Task PublishSummariesAsync(Guid boardId)
    {
        var recipients = await ReadSummariesAsync(boardId);
        if (recipients is not null) await PublishSummariesAsync(boardId, recipients);
    }

    private async Task<IReadOnlyList<BoardSummaryRecipient>?> ReadSummariesAsync(Guid boardId)
    {
        try
        {
            return await summaries.ListForBoardAsync(boardId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to build realtime board summaries after commit for board {BoardId}",
                boardId);
            return null;
        }
    }

    private async Task PublishSummariesAsync(
        Guid boardId,
        IReadOnlyList<BoardSummaryRecipient> recipients)
    {
        foreach (var recipient in recipients)
        {
            await TryPublishAsync(
                BoardRealtimeEvents.BoardSummaryChanged,
                boardId,
                entityId: null,
                recipient.UserId,
                version: null,
                () => publisher.PublishUsersAsync(
                    [recipient.UserId],
                    BoardRealtimeEvents.BoardSummaryChanged,
                    recipient.Summary));
        }
    }

    private async Task<bool> TryPublishAsync(
        string eventName,
        Guid boardId,
        Guid? entityId,
        Guid? userId,
        uint? version,
        Func<Task> publish)
    {
        logger.LogDebug(
            "Attempting realtime publication for event {EventType}, board {BoardId}, entity {EntityId}, user {UserId}, version {EntityVersion}",
            eventName,
            boardId,
            entityId,
            userId,
            version);
        try
        {
            await publish();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Realtime publication failed after commit for event {EventType}, board {BoardId}, entity {EntityId}, user {UserId}, version {EntityVersion}",
                eventName,
                boardId,
                entityId,
                userId,
                version);
            return false;
        }
    }
}
