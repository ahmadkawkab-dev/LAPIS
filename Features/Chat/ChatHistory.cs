namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public static class ChatHistory
{
    public static IQueryable<ChatMessage> Messages(WuknaDbContext db, Guid boardId) =>
        db.ChatMessages.AsNoTracking().Where(message => message.BoardId == boardId)
            .Include(message => message.SenderUser)
            .Include(message => message.Attachment)
            .Include(message => message.ScheduledTask);

    public static async Task<IResult> Page(Guid boardId, int? limit, string? before, string? after,
        string? through, HttpContext context, WuknaDbContext db, ChatCursorCodec cursors,
        TimeProvider clock, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null) return Results.NotFound();
        if (limit is < 1 or > 100 || before is not null && after is not null || through is not null && after is null)
            return Results.BadRequest(new { error = "chat_invalid_page" });
        long beforeSequence = 0, afterSequence = 0, throughSequence = 0;
        if (before is not null && !cursors.TryDecode(before, boardId, out beforeSequence) ||
            after is not null && !cursors.TryDecode(after, boardId, out afterSequence) ||
            through is not null && !cursors.TryDecode(through, boardId, out throughSequence))
            return Results.BadRequest(new { error = "chat_invalid_cursor" });

        var head = await db.ChatMessages.Where(message => message.BoardId == boardId)
            .MaxAsync(message => (long?)message.Sequence, ct) ?? 0;
        var boundary = through is null ? head : throughSequence;
        if (boundary > head || afterSequence > boundary || beforeSequence > head)
            return Results.BadRequest(new { error = "chat_invalid_cursor" });
        var query = Messages(db, boardId).Where(message => message.Sequence <= boundary);
        if (before is not null) query = query.Where(message => message.Sequence < beforeSequence);
        if (after is not null) query = query.Where(message => message.Sequence > afterSequence);
        var size = limit ?? 50;
        var rows = after is not null
            ? await query.OrderBy(message => message.Sequence).Take(size + 1).ToListAsync(ct)
            : await query.OrderByDescending(message => message.Sequence).Take(size + 1).ToListAsync(ct);
        var items = rows.Take(size).ToList();
        if (after is null) items.Reverse();
        var older = items.Count == 0 ? null : cursors.Encode(boardId, items[0].Sequence);
        // An empty catch-up must not acknowledge messages beyond its fixed boundary.
        var newer = cursors.Encode(boardId, items.Count == 0 ? afterSequence : items[^1].Sequence);
        var result = new ChatHistoryPageDto(items.Select(message => ChatMessageDto.From(message, cursors)).ToArray(),
            older, newer, rows.Count > size, cursors.Encode(boardId, boundary), clock.GetUtcNow());
        await transaction.CommitAsync(ct);
        return Results.Ok(result);
    }

    public static async Task<IResult> Get(Guid boardId, Guid messageId, HttpContext context,
        WuknaDbContext db, ChatCursorCodec cursors, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null) return Results.NotFound();
        var message = await Messages(db, boardId).SingleOrDefaultAsync(message => message.Id == messageId, ct);
        if (message is null) return Results.NotFound();
        var result = ChatMessageDto.From(message, cursors);
        await transaction.CommitAsync(ct);
        return Results.Ok(result);
    }
}
