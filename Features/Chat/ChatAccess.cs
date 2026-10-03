namespace Wukna.Features.Chat;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Board;
using Wukna.Shared.Data.AppDbContext;

public static class ChatAccess
{
    public static async Task<BoardMemberChatState> LockStateAsync(WuknaDbContext db, Guid boardId, Guid userId, CancellationToken ct)
    {
        var states = await db.BoardMemberChatStates.FromSqlInterpolated($"""
            SELECT * FROM board_member_chat_states WHERE board_id = {boardId} AND user_id = {userId} FOR UPDATE
            """).ToListAsync(ct);
        if (states.SingleOrDefault() is { } existing) return existing;
        var state = new BoardMemberChatState { BoardId = boardId, UserId = userId };
        db.BoardMemberChatStates.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }
    public static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    // Call inside a transaction. Removal cannot commit while this authorization
    // is being used. Canvas edit permissions are deliberately not part of chat access.
    public static async Task<BoardMembership?> LockMembershipAsync(
        WuknaDbContext db, Guid boardId, Guid userId, CancellationToken ct)
    {
        var memberships = await db.BoardMemberships.FromSqlInterpolated($"""
            SELECT * FROM board_memberships
            WHERE board_id = {boardId} AND user_id = {userId} FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        return memberships.SingleOrDefault();
    }

    // Board key-share precedes settings and membership locks so a concurrent
    // board deletion cannot take the board lock then deadlock on settings cascade.
    public static async Task<BoardChatSettings?> LockSettingsAsync(
        WuknaDbContext db, Guid boardId, Guid userId, CancellationToken ct)
    {
        if (!await db.BoardMemberships.AnyAsync(member => member.BoardId == boardId && member.UserId == userId, ct))
            return null;
        var boards = await db.Boards.FromSqlInterpolated($"SELECT * FROM boards WHERE id = {boardId} FOR KEY SHARE")
            .AsNoTracking().ToListAsync(ct);
        if (boards.Count == 0) return null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO board_chat_settings (board_id, slow_mode_seconds, settings_revision, last_message_sequence)
            VALUES ({boardId}, 0, 1, 0) ON CONFLICT (board_id) DO NOTHING
            """, ct);
        var settings = await db.BoardChatSettings.FromSqlInterpolated(
            $"SELECT * FROM board_chat_settings WHERE board_id = {boardId} FOR UPDATE").ToListAsync(ct);
        return settings.SingleOrDefault();
    }
}
