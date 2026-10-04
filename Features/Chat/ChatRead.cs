namespace Wukna.Features.Chat;

using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed record SetChatReadRequest(string? Cursor, Guid MembershipInstanceId)
{
    public static async ValueTask<SetChatReadRequest?> BindAsync(HttpContext context, ParameterInfo _)
        => await ChatBoundedJson.ReadAsync<SetChatReadRequest>(context);
}
public sealed record ChatReadStateDto(string LastReadSequence, int UnreadCount);

public static class ChatRead
{
    public static Task<int> CountUnreadAsync(WuknaDbContext db, Guid boardId, Guid userId,
        long afterSequence, CancellationToken ct) => db.ChatMessages.AsNoTracking().CountAsync(message =>
            message.BoardId == boardId && message.Sequence > afterSequence && message.SenderUserId != userId, ct);

    public static async Task<IResult> Update(Guid boardId, SetChatReadRequest request,
        HttpContext context, WuknaDbContext db, ChatCursorCodec cursors, TimeProvider clock, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, ct);
        if (settings is null || await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null)
            return Results.NotFound();
        if (request.MembershipInstanceId == Guid.Empty || request.Cursor is null ||
            !cursors.TryDecode(request.Cursor, boardId, out var sequence) || sequence > settings.LastMessageSequence)
            return Results.BadRequest(new { error = "chat_invalid_read_cursor" });
        var state = await ChatAccess.LockStateAsync(db, boardId, userId, ct);
        if (state.MembershipInstanceId != request.MembershipInstanceId)
            return Results.Conflict(new { error = "chat_membership_changed" });
        if (sequence > 0 && !await db.ChatMessages.AnyAsync(message =>
            message.BoardId == boardId && message.Sequence == sequence, ct))
            return Results.BadRequest(new { error = "chat_invalid_read_cursor" });
        if (sequence > state.LastReadSequence)
        {
            state.LastReadSequence = sequence;
            await db.SaveChangesAsync(ct);
            await db.Notifications.Where(item => item.UserId == userId && item.BoardId == boardId &&
                item.MembershipInstanceId == state.MembershipInstanceId && item.Type == Wukna.Features.Notifications.NotificationType.ChatActivity &&
                item.LastChatSequence <= sequence && item.ReadRevision < item.Revision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReadRevision, item => item.Revision)
                    .SetProperty(item => item.ReadAt, clock.GetUtcNow()), ct);
            Wukna.Features.Notifications.NotificationSources.StateChanged(db, userId, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        var unread = await CountUnreadAsync(db, boardId, userId, state.LastReadSequence, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new ChatReadStateDto(state.LastReadSequence.ToString(CultureInfo.InvariantCulture), unread));
    }
}
