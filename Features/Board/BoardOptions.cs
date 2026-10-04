namespace Wukna.Features.Board;

using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed class BoardOptions
{
    public int MaxGuests { get; set; } = 20;
    public int MembershipWritesPerMinute { get; set; } = 30;
}

public static class BoardMembershipLocks
{
    // All membership mutations take this aggregate lock before a member lock.
    // NO KEY UPDATE serializes invitations but remains compatible with chat's
    // board KEY SHARE lock; the board ID is never changed by these commands.
    public static async Task<bool> LockBoardAsync(WuknaDbContext db, Guid boardId, CancellationToken ct) =>
        (await db.Boards.FromSqlInterpolated($"SELECT * FROM boards WHERE id = {boardId} FOR NO KEY UPDATE")
            .AsNoTracking().ToListAsync(ct)).Count == 1;
}
