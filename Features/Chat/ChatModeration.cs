namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Wukna.Features.Board;
using Wukna.Shared.Data.AppDbContext;

public sealed record SetChatSettingsRequest(int SlowModeSeconds, long ExpectedRevision);
public sealed record SetChatMuteRequest(bool IsMuted, DateTimeOffset? MutedUntil,
    Guid MembershipInstanceId, long ExpectedRevision);
public sealed record ChatSettingsDto(int SlowModeSeconds, long SettingsRevision, DateTimeOffset ServerTime);
public sealed record ChatModerationMemberDto(ChatSenderDto Sender, BoardRole Role, Guid MembershipInstanceId,
    bool IsMuted, DateTimeOffset? MutedUntil, long ModerationRevision);
public sealed record ChatMemberPageDto(IReadOnlyList<ChatModerationMemberDto> Items, Guid? NextUserId);

public static class ChatModeration
{
    public static bool IsMuted(BoardMemberChatState state, DateTimeOffset now) =>
        state.IsMuted && (state.MutedUntil is null || state.MutedUntil > now);

    public static ChatJoinedDto Self(Guid boardId, BoardMembership member, BoardChatSettings settings,
        BoardMemberChatState state, ChatCursorCodec cursors, DateTimeOffset now, int unreadCount) => new(boardId,
        state.MembershipInstanceId, cursors.Encode(boardId, settings.LastMessageSequence),
        settings.SlowModeSeconds, settings.SettingsRevision, IsMuted(state, now), state.MutedUntil, now,
        state.ModerationRevision, member.Role == BoardRole.Owner || settings.SlowModeSeconds == 0 ||
        state.CooldownSettingsRevision != settings.SettingsRevision ? null : state.NextSendAllowedAt,
        member.Role == BoardRole.Owner, state.LastReadSequence.ToString(System.Globalization.CultureInfo.InvariantCulture), unreadCount);

    public static async Task<IResult> State(Guid boardId, HttpContext context, WuknaDbContext db,
        ChatCursorCodec cursors, TimeProvider clock, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, ct);
        if (settings is null) return Results.NotFound();
        var member = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
        if (member is null) return Results.NotFound();
        var state = await ChatAccess.LockStateAsync(db, boardId, userId, ct);
        var unread = await ChatRead.CountUnreadAsync(db, boardId, userId, state.LastReadSequence, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(Self(boardId, member, settings, state, cursors, clock.GetUtcNow(), unread));
    }

    public static async Task<IResult> Settings(Guid boardId, SetChatSettingsRequest request,
        HttpContext context, WuknaDbContext db, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, ct);
        if (settings is null) return Results.NotFound();
        var caller = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
        if (caller is null) return Results.NotFound();
        if (caller.Role != BoardRole.Owner) return Results.Forbid();
        if (request.SlowModeSeconds is < 0 or > 21600 || request.ExpectedRevision < 1)
            return Results.BadRequest(new { error = "chat_invalid_settings" });
        // Desired-state PUT is replay-safe; a lost reply does not create another revision/event.
        if (settings.SlowModeSeconds != request.SlowModeSeconds)
        {
            if (settings.SettingsRevision != request.ExpectedRevision)
                return Results.Conflict(new { error = "chat_settings_conflict" });
            settings.SlowModeSeconds = request.SlowModeSeconds;
            settings.SettingsRevision = checked(settings.SettingsRevision + 1);
            db.ChatOutboxEvents.Add(Event(boardId, ChatOutboxEventKind.SettingsChanged, settings.SettingsRevision, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        logs.CreateLogger("Wukna.Features.Chat.Moderation").LogInformation(
            "Chat settings updated for board {BoardId}, revision {Revision}", boardId, settings.SettingsRevision);
        return Results.Ok(new ChatSettingsDto(settings.SlowModeSeconds, settings.SettingsRevision, clock.GetUtcNow()));
    }

    public static async Task<IResult> Members(Guid boardId, Guid? afterUserId, HttpContext context,
        WuknaDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ChatAccess.LockSettingsAsync(db, boardId, userId, ct) is null) return Results.NotFound();
        var caller = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
        if (caller is null) return Results.NotFound();
        if (caller.Role != BoardRole.Owner) return Results.Forbid();
        var rows = await db.BoardMemberships.FromSqlInterpolated($"""
            SELECT * FROM board_memberships WHERE board_id = {boardId}
              AND ({afterUserId == null} OR user_id > {afterUserId ?? Guid.Empty})
            ORDER BY user_id LIMIT 101 FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        var page = rows.Take(100).ToArray();
        var ids = page.Select(member => member.UserId).ToArray();
        var states = await db.BoardMemberChatStates.Where(state => state.BoardId == boardId && ids.Contains(state.UserId))
            .ToDictionaryAsync(state => state.UserId, ct);
        foreach (var member in page.Where(member => !states.ContainsKey(member.UserId)))
        {
            var state = new BoardMemberChatState { BoardId = boardId, UserId = member.UserId };
            db.BoardMemberChatStates.Add(state); states.Add(member.UserId, state);
        }
        await db.SaveChangesAsync(ct);
        var senders = await db.Users.AsNoTracking().Where(user => ids.Contains(user.Id)).ToDictionaryAsync(user => user.Id, ct);
        var items = page.Select(member => Member(ChatSenderDto.From(senders[member.UserId]), member.Role,
            states[member.UserId], clock.GetUtcNow())).ToArray();
        await transaction.CommitAsync(ct);
        return Results.Ok(new ChatMemberPageDto(items, rows.Count > 100 ? page[^1].UserId : null));
    }

    public static async Task<IResult> Mute(Guid boardId, Guid memberId, SetChatMuteRequest request,
        HttpContext context, WuknaDbContext db, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ChatAccess.LockSettingsAsync(db, boardId, userId, ct) is null) return Results.NotFound();
        var caller = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
        if (caller is null) return Results.NotFound();
        if (caller.Role != BoardRole.Owner) return Results.Forbid();
        var member = await ChatAccess.LockMembershipAsync(db, boardId, memberId, ct);
        if (member is null) return Results.NotFound();
        if (member.Role == BoardRole.Owner) return Results.Conflict(new { error = "chat_owner_protected" });
        var now = clock.GetUtcNow();
        if (request.ExpectedRevision < 0 || request.MembershipInstanceId == Guid.Empty ||
            !request.IsMuted && request.MutedUntil is not null || request.IsMuted && request.MutedUntil <= now)
            return Results.BadRequest(new { error = "chat_invalid_mute" });
        var state = await ChatAccess.LockStateAsync(db, boardId, memberId, ct);
        if (state.MembershipInstanceId != request.MembershipInstanceId)
            return Results.Conflict(new { error = "chat_membership_changed" });
        var until = request.MutedUntil is { } value ? new DateTimeOffset(value.UtcTicks / 10 * 10, TimeSpan.Zero) : (DateTimeOffset?)null;
        if (state.IsMuted != request.IsMuted || state.MutedUntil != until)
        {
            if (state.ModerationRevision != request.ExpectedRevision)
                return Results.Conflict(new { error = "chat_moderation_conflict" });
            state.IsMuted = request.IsMuted; state.MutedUntil = until;
            state.ModerationRevision = checked(state.ModerationRevision + 1);
            var notification = Event(boardId, ChatOutboxEventKind.MemberStateChanged, state.ModerationRevision, now);
            notification.MemberUserId = memberId; notification.MembershipInstanceId = state.MembershipInstanceId;
            db.ChatOutboxEvents.Add(notification);
            await db.SaveChangesAsync(ct);
        }
        var result = Member(ChatSenderDto.From(await db.Users.AsNoTracking().SingleAsync(user => user.Id == memberId, ct)),
            member.Role, state, now);
        await transaction.CommitAsync(ct);
        logs.CreateLogger("Wukna.Features.Chat.Moderation").LogInformation(
            "Chat mute changed for board {BoardId}, member {MemberId}, revision {Revision}", boardId, memberId, state.ModerationRevision);
        return Results.Ok(result);
    }

    private static ChatModerationMemberDto Member(ChatSenderDto sender, BoardRole role, BoardMemberChatState state,
        DateTimeOffset now) => new(sender, role, state.MembershipInstanceId, IsMuted(state, now), state.MutedUntil, state.ModerationRevision);
    private static ChatOutboxEvent Event(Guid boardId, ChatOutboxEventKind kind, long revision, DateTimeOffset now) =>
        new() { BoardId = boardId, Kind = kind, Revision = revision, CreatedAt = now, NextAttemptAt = now };
}
