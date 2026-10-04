namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Wukna.Features.Board;
using Wukna.Shared.Data.AppDbContext;

public sealed record ChatRecipients(IReadOnlyList<ChatSubscription> Subscriptions,
    IReadOnlyDictionary<Guid, BoardMembership> Members, IReadOnlyDictionary<Guid, BoardMemberChatState> States)
{
    // Caller holds a transaction through transport enqueue. Groups never authorize private data.
    public static async Task<ChatRecipients> LockAsync(WuknaDbContext db, ChatConnectionRegistry connections,
        Guid boardId, CancellationToken ct)
    {
        var members = await db.BoardMemberships.FromSqlInterpolated($"""
            SELECT * FROM board_memberships WHERE board_id = {boardId} ORDER BY user_id FOR SHARE
            """).AsNoTracking().ToDictionaryAsync(member => member.UserId, ct);
        var states = await db.BoardMemberChatStates.FromSqlInterpolated($"""
            SELECT * FROM board_member_chat_states WHERE board_id = {boardId} ORDER BY user_id FOR SHARE
            """).AsNoTracking().ToDictionaryAsync(state => state.UserId, ct);
        var subscriptions = connections.ForBoard(boardId).Where(subscription => members.ContainsKey(subscription.UserId) &&
            states.TryGetValue(subscription.UserId, out var state) && state.MembershipInstanceId == subscription.MembershipInstanceId).ToArray();
        return new(subscriptions, members, states);
    }
}
