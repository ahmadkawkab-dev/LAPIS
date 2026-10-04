namespace Wukna.Features.Chat;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Board;
using Wukna.Shared.Data.AppDbContext;
using Wukna.Features.Notifications;

public static class SendChatMessage
{
    public static async Task<IResult> Handle(Guid boardId, SendChatMessageRequest request,
        HttpContext context, WuknaDbContext db, ChatCursorCodec cursors, TimeProvider clock,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, ct);
        if (settings is null) return Results.NotFound();
        var membership = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
        if (membership is null) return Results.NotFound();
        if (request.ClientMessageId == Guid.Empty || request.Body is null || request.Body.Length > 4000 ||
            string.IsNullOrWhiteSpace(request.Body))
            return Results.BadRequest(new { error = "chat_invalid_message" });
        var body = request.Body.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (request.Mentions?.Any(item => item is null) == true) return Results.BadRequest(new { error = "chat_invalid_mentions" });
        var mentions = (request.Mentions ?? []).OrderBy(item => item.Start).ToArray();
        if (mentions.Length > 10 || body.Contains('\0')) return Results.BadRequest(new { error = "chat_invalid_mentions" });
        var payload = mentions.Length == 0 && request.ReplyToMessageId is null
            ? JsonSerializer.Serialize(new { type = "text", body })
            : JsonSerializer.Serialize(new { type = "text", body, mentions, request.ReplyToMessageId,
                notifyReplyAuthor = request.ReplyToMessageId is not null && request.NotifyReplyAuthor });
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            payload)));
        // Match PostgreSQL microsecond precision so the first response and replay
        // return identical authoritative timestamps.
        var current = clock.GetUtcNow();
        var now = new DateTimeOffset(current.UtcTicks / 10 * 10, TimeSpan.Zero);
        var previous = await ChatHistory.Messages(db, boardId).SingleOrDefaultAsync(message =>
            message.SenderUserId == userId && message.ClientMessageId == request.ClientMessageId, ct);
        var states = await db.BoardMemberChatStates.FromSqlInterpolated($"""
            SELECT * FROM board_member_chat_states
            WHERE board_id = {boardId} AND user_id = {userId} FOR UPDATE
            """).ToListAsync(ct);
        var state = states.SingleOrDefault();
        if (previous is not null)
        {
            if (previous.RequestFingerprint != fingerprint)
                return Results.Conflict(new { error = "chat_operation_conflict" });
            var replay = new ChatSendResultDto(ChatMessageDto.From(previous, cursors), now,
                ChatSendPolicy.NextSend(settings, state, membership.Role), true, settings.SettingsRevision);
            await transaction.CommitAsync(ct);
            return Results.Ok(replay);
        }
        if (ChatSendPolicy.Rejection(context, settings, state, membership.Role, now) is { } rejection) return rejection;
        var lastEnd = 0;
        foreach (var mention in mentions)
        {
            if (mention.Start < lastEnd || mention.Length < 2 || mention.Start > body.Length - mention.Length)
                return Results.BadRequest(new { error = "chat_invalid_mentions" });
            var target = await db.BoardMemberships.Where(item => item.BoardId == boardId && item.UserId == mention.UserId)
                .Select(item => item.User.Username).SingleOrDefaultAsync(ct);
            if (target is null || body.Substring(mention.Start, mention.Length) != "@" + target)
                return Results.BadRequest(new { error = "chat_invalid_mentions" });
            lastEnd = mention.Start + mention.Length;
        }
        ChatMessage? parent = null;
        if (request.ReplyToMessageId is { } parentId)
        {
            parent = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == parentId && item.BoardId == boardId, ct);
            if (parent is null || parent.Type == ChatMessageType.ScheduledTask && parent.CreatedAt.AddHours(24) <= now)
                return Results.BadRequest(new { error = "chat_invalid_reply" });
        }
        if (state is null) state = await ChatAccess.LockStateAsync(db, boardId, userId, ct);
        ChatSendPolicy.Consume(settings, state, membership.Role, now);
        var message = new ChatMessage
        {
            BoardId = boardId, SenderUserId = userId, Sequence = checked(++settings.LastMessageSequence),
            Type = ChatMessageType.Text, Body = body, CreatedAt = now,
            ClientMessageId = request.ClientMessageId, RequestFingerprint = fingerprint,
            MentionsJson = JsonSerializer.Serialize(mentions), ReplyToMessageId = parent?.Id,
            ReplyAuthorUserId = parent?.SenderUserId, NotifyReplyAuthor = parent is not null && request.NotifyReplyAuthor,
            SenderUser = await db.Users.SingleAsync(user => user.Id == userId, ct)
        };
        db.ChatMessages.Add(message);
        db.ChatOutboxEvents.Add(new ChatOutboxEvent
        {
            BoardId = boardId, Kind = ChatOutboxEventKind.MessageCreated,
            MessageId = message.Id, MessageSequence = message.Sequence, CreatedAt = now, NextAttemptAt = now
        });
        try
        {
            await NotificationSources.ChatAsync(db, message, now, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logs.CreateLogger("Wukna.Features.Chat.SendChatMessage").LogError(
                "Chat persistence failed for board {BoardId}, user {UserId}, operation {OperationId}: {FailureType}",
                boardId, userId, request.ClientMessageId, exception.GetType().Name);
            throw;
        }
        return Results.Created($"/api/boards/{boardId}/chat/messages/{message.Id}",
            new ChatSendResultDto(ChatMessageDto.From(message, cursors), now, state.NextSendAllowedAt, false, settings.SettingsRevision));
    }

}
