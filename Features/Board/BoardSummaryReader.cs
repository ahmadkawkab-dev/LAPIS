namespace Wukna.Features.Board;

using Wukna.Features.NoteConnection;
using Wukna.Features.Notes;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.EntityFrameworkCore;

public sealed class BoardSummaryReader(WuknaDbContext db)
{
    public const int PreviewNodeLimit = 16;
    public const int PreviewConnectionLimit = 16;

    private sealed record BoardMetadata(
        Guid UserId,
        Guid Id,
        string Title,
        BoardRole Role,
        bool CanEdit,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int NoteCount,
        int TaskListCount,
        int TaskItemCount,
        int CompletedTaskItemCount,
        int MemberCount,
        string? CardColor,
        int CardColorVersion);

    private sealed record PreviewNodeRow(
        Guid BoardId,
        Guid Id,
        BoardPreviewNodeType Type,
        double X,
        double Y,
        double Width,
        double Height,
        string Color);

    private sealed record PreviewConnectionRow(
        Guid BoardId,
        Guid SourceId,
        Guid TargetId,
        ConnectionType Type);

    public async Task<IReadOnlyList<BoardListItemDto>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        (await ReadAsync(
            db.BoardMemberships.AsNoTracking()
                .Where(membership => membership.UserId == userId),
            cancellationToken))
        .Select(recipient => recipient.Summary)
        .ToArray();

    public Task<IReadOnlyList<BoardSummaryRecipient>> ListForBoardAsync(
        Guid boardId,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            db.BoardMemberships.AsNoTracking()
                .Where(membership => membership.BoardId == boardId),
            cancellationToken);

    private async Task<IReadOnlyList<BoardSummaryRecipient>> ReadAsync(
        IQueryable<BoardMembership> memberships,
        CancellationToken cancellationToken)
    {
        var boards = await memberships
            .OrderByDescending(membership => membership.Board.UpdatedAt)
            .ThenByDescending(membership => membership.BoardId)
            .Select(membership => new BoardMetadata(
                membership.UserId,
                membership.BoardId,
                membership.Board.Title,
                membership.Role,
                membership.Role == BoardRole.Owner || membership.CanEdit,
                membership.Board.CreatedAt,
                membership.Board.UpdatedAt,
                membership.Board.Notes.Count(note => note.Kind == NoteKind.Standalone),
                membership.Board.Notes.Count(note => note.Kind == NoteKind.List),
                membership.Board.Notes.Count(note => note.Kind == NoteKind.ChecklistItem),
                membership.Board.Notes.Count(note =>
                    note.Kind == NoteKind.ChecklistItem && note.IsCompleted),
                membership.Board.Memberships.Count,
                membership.Board.CardColor,
                membership.Board.CardColorVersion))
            .ToListAsync(cancellationToken);

        if (boards.Count == 0) return [];

        var boardIds = boards.Select(board => board.Id).ToArray();
        var nodes = await db.Boards.AsNoTracking()
            .Where(board => boardIds.Contains(board.Id))
            .SelectMany(board => board.Notes
                .Where(note => note.Kind != NoteKind.ChecklistItem)
                .OrderBy(note => note.CreatedAt)
                .ThenBy(note => note.Id)
                .Take(PreviewNodeLimit)
                .Select(note => new PreviewNodeRow(
                    board.Id,
                    note.Id,
                    note.Kind == NoteKind.List
                        ? BoardPreviewNodeType.TaskList
                        : BoardPreviewNodeType.Note,
                    note.PositionX!.Value,
                    note.PositionY!.Value,
                    note.Width,
                    note.Height,
                    note.Color)))
            .ToListAsync(cancellationToken);

        var selectedNodeIds = nodes.Select(node => node.Id).ToArray();
        var connections = selectedNodeIds.Length == 0
            ? []
            : await db.Boards.AsNoTracking()
                .Where(board => boardIds.Contains(board.Id))
                .SelectMany(board => db.NoteConnections
                    .Where(connection => connection.BoardId == board.Id &&
                        selectedNodeIds.Contains(connection.SourceNoteId) &&
                        selectedNodeIds.Contains(connection.TargetNoteId))
                    .OrderBy(connection => connection.CreatedAt)
                    .ThenBy(connection => connection.Id)
                    .Take(PreviewConnectionLimit)
                    .Select(connection => new PreviewConnectionRow(
                        board.Id,
                        connection.SourceNoteId,
                        connection.TargetNoteId,
                        connection.Type)))
                .ToListAsync(cancellationToken);

        var nodesByBoard = nodes
            .GroupBy(node => node.BoardId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<BoardPreviewNodeDto>)group.Select(node =>
                    new BoardPreviewNodeDto(
                        node.Id,
                        node.Type,
                        node.X,
                        node.Y,
                        node.Width,
                        node.Height,
                        node.Color)).ToArray());
        var connectionsByBoard = connections
            .GroupBy(connection => connection.BoardId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<BoardPreviewConnectionDto>)group.Select(connection =>
                    new BoardPreviewConnectionDto(
                        connection.SourceId,
                        connection.TargetId,
                        connection.Type)).ToArray());

        return boards.Select(board => new BoardSummaryRecipient(
            board.UserId,
            new BoardListItemDto(
                board.Id,
                board.Title,
                board.Role,
                board.CanEdit,
                board.CreatedAt,
                board.UpdatedAt,
                board.NoteCount,
                board.TaskListCount,
                board.TaskItemCount,
                board.CompletedTaskItemCount,
                board.MemberCount,
                nodesByBoard.GetValueOrDefault(board.Id) ?? [],
                connectionsByBoard.GetValueOrDefault(board.Id) ?? [],
                board.CardColor,
                board.CardColorVersion))).ToArray();
    }
}
