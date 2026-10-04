namespace Wukna.Features.Chat;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;
public static class ChatMentionMembers
{
    public static async Task<IResult> Get(Guid boardId, string? search, HttpContext context, WuknaDbContext db, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        if (search?.Length > 30) return Results.BadRequest();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ChatAccess.LockSettingsAsync(db, boardId, userId, ct) is null ||
            await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null) return Results.NotFound();
        var query = db.BoardMemberships.AsNoTracking().Where(item => item.BoardId == boardId);
        if (!string.IsNullOrEmpty(search)) query = query.Where(item => item.User.Username.StartsWith(search));
        var members = await query.OrderBy(item => item.User.Username).Take(50).Select(item =>
            new { item.UserId, username = item.User.Username, displayName = item.User.DisplayName }).ToArrayAsync(ct);
        await transaction.CommitAsync(ct); return Results.Ok(members);
    }
}
