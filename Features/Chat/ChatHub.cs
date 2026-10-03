namespace Wukna.Features.Chat;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed record ChatJoinedDto(Guid BoardId, Guid MembershipInstanceId, string LatestCursor,
    int SlowModeSeconds, long SettingsRevision, bool IsMuted, DateTimeOffset? MutedUntil, DateTimeOffset ServerTime,
    long ModerationRevision, DateTimeOffset? NextSendAllowedAt, bool IsOwner,
    string LastReadSequence, int UnreadCount);

[Authorize]
public sealed class ChatHub(WuknaDbContext db, ChatConnectionRegistry connections,
    ChatCursorCodec cursors, TimeProvider clock, ILogger<ChatHub> logger, ChatTypingService typing) : Hub
{
    public const string Path = "/hubs/chat";
    public static string Group(Guid boardId) => $"chat:{boardId:D}";

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId();
        connections.Connect(Context.ConnectionId, userId);
        logger.LogInformation("Chat connected for user {UserId}, connection {ConnectionId}", userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public async Task<ChatJoinedDto> JoinBoard(Guid boardId)
    {
        CheckInvocation();
        var userId = CurrentUserId();
        await using var transaction = await db.Database.BeginTransactionAsync(Context.ConnectionAborted);
        var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, Context.ConnectionAborted);
        var member = settings is null ? null : await ChatAccess.LockMembershipAsync(db, boardId, userId, Context.ConnectionAborted);
        if (settings is null || member is null)
        {
            logger.LogWarning("Chat join rejected for board {BoardId}, user {UserId}", boardId, userId);
            throw new HubException("chat_forbidden");
        }
        var states = await db.BoardMemberChatStates.FromSqlInterpolated($"""
            SELECT * FROM board_member_chat_states
            WHERE board_id = {boardId} AND user_id = {userId} FOR UPDATE
            """).ToListAsync(Context.ConnectionAborted);
        var state = states.SingleOrDefault();
        if (state is null)
        {
            state = new BoardMemberChatState { BoardId = boardId, UserId = userId };
            db.BoardMemberChatStates.Add(state);
            await db.SaveChangesAsync(Context.ConnectionAborted);
        }
        var subscription = new ChatSubscription(Context.ConnectionId, userId, boardId, state.MembershipInstanceId);
        if (!connections.Subscribe(subscription)) throw new HubException("chat_subscription_limit");
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(boardId), Context.ConnectionAborted);
            var unread = await ChatRead.CountUnreadAsync(db, boardId, userId, state.LastReadSequence, Context.ConnectionAborted);
            var result = ChatModeration.Self(boardId, member, settings, state, cursors, clock.GetUtcNow(), unread);
            await transaction.CommitAsync(Context.ConnectionAborted);
            return result;
        }
        catch
        {
            connections.Remove(Context.ConnectionId, boardId, state.MembershipInstanceId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(boardId), CancellationToken.None);
            throw;
        }
    }

    public async Task LeaveBoard(Guid boardId)
    {
        CheckInvocation();
        connections.Remove(Context.ConnectionId, boardId);
        await typing.EndConnectionAsync(db, Context.ConnectionId, boardId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(boardId), Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Disconnect(Context.ConnectionId);
        await typing.EndConnectionAsync(db, Context.ConnectionId);
        logger.LogInformation("Chat disconnected for connection {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task SetTyping(Guid boardId, bool active)
    {
        if (!connections.AllowTypingInvocation(Context.ConnectionId))
        {
            logger.LogWarning("Chat typing invocation limit rejected connection {ConnectionId}", Context.ConnectionId);
            throw new HubException("chat_typing_rate_limited");
        }
        return typing.SetAsync(db, Context.ConnectionId, CurrentUserId(), boardId, active, Context.ConnectionAborted);
    }

    private void CheckInvocation()
    {
        if (connections.AllowInvocation(Context.ConnectionId)) return;
        logger.LogWarning("Chat hub invocation limit rejected connection {ConnectionId}", Context.ConnectionId);
        throw new HubException("chat_hub_rate_limited");
    }

    private Guid CurrentUserId() =>
        Guid.TryParse(Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId)
            ? userId : throw new HubException("chat_unauthenticated");
}
