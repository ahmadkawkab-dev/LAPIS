namespace Wukna.Features.Chat;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;
using Wukna.Features.Notifications;

public sealed record CreateScheduledChatTaskRequest(Guid ClientMessageId, string? Title, string? Description,
    DateTime? LocalStart, DateTime? LocalEnd, string? TimeZoneId,
    int? StartOffsetMinutes, int? EndOffsetMinutes)
{
    public static async ValueTask<CreateScheduledChatTaskRequest?> BindAsync(HttpContext context, ParameterInfo _)
        => await ChatBoundedJson.ReadAsync<CreateScheduledChatTaskRequest>(context);
}

public static class CreateScheduledChatTask
{
    public static async Task<IResult> Handle(Guid boardId, CreateScheduledChatTaskRequest request,
        HttpContext context, WuknaDbContext db, ChatCursorCodec cursors, TimeProvider clock,
        CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, ct);
        if (settings is null) return Results.NotFound();
        var membership = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
        if (membership is null) return Results.NotFound();
        var error = Validate(request, out var startUtc, out var endUtc, out var startOffset);
        if (error is not null) return Results.BadRequest(new { error });
        var title = request.Title!.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { type = "scheduledTask", title, description, startUtc, endUtc,
                timeZoneId = request.TimeZoneId, startOffset }))));
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
            await transaction.CommitAsync(ct);
            return Results.Ok(new ChatSendResultDto(ChatMessageDto.From(previous, cursors), now,
                ChatSendPolicy.NextSend(settings, state, membership.Role), true, settings.SettingsRevision));
        }
        if (ChatSendPolicy.Rejection(context, settings, state, membership.Role, now) is { } rejection) return rejection;
        if (state is null) state = await ChatAccess.LockStateAsync(db, boardId, userId, ct);
        ChatSendPolicy.Consume(settings, state, membership.Role, now);
        var message = new ChatMessage
        {
            BoardId = boardId, SenderUserId = userId, Sequence = checked(++settings.LastMessageSequence),
            Type = ChatMessageType.ScheduledTask, Body = null, CreatedAt = now,
            ClientMessageId = request.ClientMessageId, RequestFingerprint = fingerprint,
            SenderUser = await db.Users.SingleAsync(user => user.Id == userId, ct),
            ScheduledTask = new ScheduledChatTask
            {
                Title = title, Description = description, StartsAtUtc = startUtc,
                EndsAtUtc = endUtc, TimeZoneId = request.TimeZoneId!, OriginalOffsetMinutes = startOffset
            }
        };
        db.ChatMessages.Add(message);
        db.ChatOutboxEvents.Add(new ChatOutboxEvent
        {
            BoardId = boardId, Kind = ChatOutboxEventKind.MessageCreated,
            MessageId = message.Id, MessageSequence = message.Sequence, CreatedAt = now, NextAttemptAt = now
        });
        await NotificationSources.ChatAsync(db, message, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/boards/{boardId}/chat/messages/{message.Id}",
            new ChatSendResultDto(ChatMessageDto.From(message, cursors), now, state.NextSendAllowedAt, false, settings.SettingsRevision));
    }

    private static string? Validate(CreateScheduledChatTaskRequest request,
        out DateTimeOffset startUtc, out DateTimeOffset? endUtc, out int startOffset)
    {
        startUtc = default; endUtc = null; startOffset = 0;
        if (request.ClientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Trim().Length > 200 || request.Title.Contains('\0') ||
            request.Description?.Length > 4000 || request.Description?.Contains('\0') == true ||
            request.LocalStart is null || string.IsNullOrWhiteSpace(request.TimeZoneId) ||
            request.TimeZoneId.Length > 100 || request.TimeZoneId != request.TimeZoneId.Trim() ||
            request.LocalEnd is null && request.EndOffsetMinutes is not null)
            return "chat_invalid_scheduled_task";
        // Linux deployment uses IANA zone identifiers; UTC is a standard special case.
        if (request.TimeZoneId != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(request.TimeZoneId, out _))
            return "chat_invalid_time_zone";
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return "chat_invalid_time_zone"; }
        catch (InvalidTimeZoneException) { return "chat_invalid_time_zone"; }
        var startError = Resolve(request.LocalStart.Value, request.StartOffsetMinutes, zone, out startUtc, out startOffset);
        if (startError is not null) return startError;
        if (request.LocalEnd is { } localEnd)
        {
            var endError = Resolve(localEnd, request.EndOffsetMinutes, zone, out var resolvedEnd, out _);
            if (endError is not null) return endError;
            endUtc = resolvedEnd;
            if (endUtc <= startUtc) return "chat_invalid_task_range";
        }
        return null;
    }

    private static string? Resolve(DateTime local, int? selectedMinutes, TimeZoneInfo zone,
        out DateTimeOffset utc, out int offsetMinutes)
    {
        utc = default; offsetMinutes = 0;
        if (local.Kind != DateTimeKind.Unspecified) return "chat_invalid_scheduled_task";
        if (zone.IsInvalidTime(local)) return "chat_invalid_local_time";
        if (selectedMinutes is < -840 or > 840) return "chat_invalid_offset";
        var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        if (offsets.Length > 1 && selectedMinutes is null) return "chat_ambiguous_local_time";
        var chosen = selectedMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : offsets[0];
        if (!offsets.Contains(chosen)) return "chat_invalid_offset";
        try { utc = new DateTimeOffset(local, chosen).ToUniversalTime(); }
        catch (ArgumentException) { return "chat_invalid_scheduled_task"; }
        offsetMinutes = (int)chosen.TotalMinutes;
        return null;
    }
}
