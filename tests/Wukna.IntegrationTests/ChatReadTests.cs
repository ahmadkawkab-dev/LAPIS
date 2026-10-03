namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wukna.Features.Chat;
using Xunit;

public sealed class ChatReadTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Read_cursor_is_board_scoped_monotonic_and_excludes_own_posts_from_unread_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await ChatControlTestSupport.Seed(postgres, ct);
        await using var factory = new ChatControlTestSupport.Factory(postgres, seed.Clock);
        using var owner = ChatControlTestSupport.Client(factory, seed.Owner);
        using var guest = ChatControlTestSupport.Client(factory, seed.Guest);
        using var peer = ChatControlTestSupport.Client(factory, seed.Peer);
        using var outsider = ChatControlTestSupport.Client(factory, seed.Outsider);
        var path = ChatControlTestSupport.Path(seed);
        using var first = await ChatControlTestSupport.Send(owner, seed, ct);
        using var second = await ChatControlTestSupport.Send(guest, seed, ct);
        using var third = await ChatControlTestSupport.Send(peer, seed, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        var firstCursor = (await first.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message.Cursor;
        var secondCursor = (await second.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message.Cursor;
        var thirdCursor = (await third.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message.Cursor;
        var state = await ChatControlTestSupport.State(guest, seed, ct);
        Assert.Equal("0", state.LastReadSequence);
        Assert.Equal(2, state.UnreadCount);
        await using (var live = ChatControlTestSupport.Connection(factory, seed.Guest))
        {
            var joined = await ChatControlTestSupport.Join(live, seed, ct);
            Assert.Equal("0", joined.LastReadSequence);
            Assert.Equal(2, joined.UnreadCount);
            await live.StopAsync(ct);
        }

        using var wrongBoard = await guest.PutAsJsonAsync(path + "/read",
            new SetChatReadRequest(factory.Services.GetRequiredService<ChatCursorCodec>().Encode(Guid.NewGuid(), 1),
                state.MembershipInstanceId), ct);
        using var future = await guest.PutAsJsonAsync(path + "/read",
            new SetChatReadRequest(factory.Services.GetRequiredService<ChatCursorCodec>().Encode(seed.Board.Id, 100),
                state.MembershipInstanceId), ct);
        using var outsiderRead = await outsider.PutAsJsonAsync(path + "/read",
            new SetChatReadRequest(firstCursor, state.MembershipInstanceId), ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongBoard.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderRead.StatusCode);

        using var markFirst = await guest.PutAsJsonAsync(path + "/read",
            new SetChatReadRequest(firstCursor, state.MembershipInstanceId), ct);
        Assert.Equal(HttpStatusCode.OK, markFirst.StatusCode);
        var read = (await markFirst.Content.ReadFromJsonAsync<ChatReadStateDto>(ct))!;
        Assert.Equal("1", read.LastReadSequence);
        Assert.Equal(1, read.UnreadCount);
        var parallel = await Task.WhenAll(
            guest.PutAsJsonAsync(path + "/read", new SetChatReadRequest(thirdCursor, state.MembershipInstanceId), ct),
            guest.PutAsJsonAsync(path + "/read", new SetChatReadRequest(secondCursor, state.MembershipInstanceId), ct));
        try { Assert.All(parallel, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in parallel) response.Dispose(); }
        using var replayOld = await guest.PutAsJsonAsync(path + "/read",
            new SetChatReadRequest(firstCursor, state.MembershipInstanceId), ct);
        Assert.Equal(HttpStatusCode.OK, replayOld.StatusCode);
        read = (await replayOld.Content.ReadFromJsonAsync<ChatReadStateDto>(ct))!;
        Assert.Equal("3", read.LastReadSequence);
        Assert.Equal(0, read.UnreadCount);
        Assert.Equal("3", (await ChatControlTestSupport.State(guest, seed, ct)).LastReadSequence);
        await using var db = postgres.CreateContext();
        Assert.Equal(3, (await db.BoardMemberChatStates.SingleAsync(item =>
            item.BoardId == seed.Board.Id && item.UserId == seed.Guest.Id, ct)).LastReadSequence);
    }

    [Fact]
    public async Task Removed_and_reinvited_member_cannot_use_an_old_read_cursor_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await ChatControlTestSupport.Seed(postgres, ct);
        await using var factory = new ChatControlTestSupport.Factory(postgres, seed.Clock);
        using var owner = ChatControlTestSupport.Client(factory, seed.Owner);
        using var guest = ChatControlTestSupport.Client(factory, seed.Guest);
        using var sent = await ChatControlTestSupport.Send(owner, seed, ct);
        var cursor = (await sent.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message.Cursor;
        var before = await ChatControlTestSupport.State(guest, seed, ct);
        using var removal = await ChatControlTestSupport.Remove(owner, seed, seed.Guest, ct);
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        using var denied = await guest.PutAsJsonAsync(ChatControlTestSupport.Path(seed) + "/read",
            new SetChatReadRequest(cursor, before.MembershipInstanceId), ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var reinvite = await ChatControlTestSupport.Invite(owner, seed, seed.Guest, ct);
        Assert.True(reinvite.IsSuccessStatusCode);
        var after = await ChatControlTestSupport.State(guest, seed, ct);
        Assert.NotEqual(before.MembershipInstanceId, after.MembershipInstanceId);
        Assert.Equal("0", after.LastReadSequence);
        Assert.Equal(1, after.UnreadCount);
        using var stale = await guest.PutAsJsonAsync(ChatControlTestSupport.Path(seed) + "/read",
            new SetChatReadRequest(cursor, before.MembershipInstanceId), ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("0", (await ChatControlTestSupport.State(guest, seed, ct)).LastReadSequence);
    }
}
