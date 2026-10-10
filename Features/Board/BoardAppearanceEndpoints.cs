namespace Wukna.Features.Board;

using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Chat;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;

public sealed record SetBoardAppearanceRequest(
    [property: JsonRequired] string? CardColor,
    [property: JsonRequired] int ExpectedVersion);

public static class BoardAppearanceEndpoints
{
    public static void MapBoardAppearanceEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/{boardId:guid}/appearance", async (
            Guid boardId, SetBoardAppearanceRequest request, HttpContext context,
            WuknaDbContext db, BoardActivity activity, BoardRealtimeDispatcher realtime,
            CancellationToken ct) =>
        {
            if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (request.ExpectedVersion < 0 || request.CardColor is not
                (null or "sage" or "blue" or "lavender" or "clay" or "gold" or "rose"))
                return Results.BadRequest(new { error = "invalid_board_color" });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Match membership writers' board-before-membership lock order. The locks
            // serialize color writes and keep permission checks valid through commit.
            if (!await BoardMembershipLocks.LockBoardAsync(db, boardId, ct)) return Results.NotFound();
            var member = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
            if (member is null) return Results.NotFound();
            if (member.Role != BoardRole.Owner && !member.CanEdit) return Results.Forbid();
            var board = await db.Boards.SingleAsync(candidate => candidate.Id == boardId, ct);
            if (board.CardColorVersion != request.ExpectedVersion)
                return Results.Conflict(new { error = "board_appearance_conflict", board.CardColor, board.CardColorVersion });

            var changed = board.CardColor != request.CardColor;
            if (changed)
            {
                board.CardColor = request.CardColor;
                board.CardColorVersion++;
                activity.MarkUpdated(db, boardId);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            if (changed)
                await realtime.BoardUpdatedAsync(new BoardUpdatedEvent(
                    board.Id, board.Title, board.UpdatedAt, board.CardColor, board.CardColorVersion));
            return Results.Ok(new BoardDetailDto(board.Id, board.Title, board.CreatedAt,
                board.UpdatedAt, member.Role, true, board.CardColor, board.CardColorVersion));
        });
    }
}
