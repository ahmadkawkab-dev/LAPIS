namespace Wukna.Features.Board;

using Wukna.Features.NoteConnection;

public sealed record BoardDetailDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    BoardRole Role,
    bool CanEdit,
    string? CardColor = null,
    int CardColorVersion = 0);

public enum BoardPreviewNodeType
{
    Note = 0,
    TaskList = 1
}

public sealed record BoardPreviewNodeDto(
    Guid Id,
    BoardPreviewNodeType Type,
    double X,
    double Y,
    double Width,
    double Height,
    string Color);

public sealed record BoardPreviewConnectionDto(
    Guid SourceId,
    Guid TargetId,
    ConnectionType Type);

public sealed record BoardListItemDto(
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
    IReadOnlyList<BoardPreviewNodeDto> PreviewNodes,
    IReadOnlyList<BoardPreviewConnectionDto> PreviewConnections,
    string? CardColor = null,
    int CardColorVersion = 0);

public sealed record BoardSummaryRecipient(Guid UserId, BoardListItemDto Summary);
