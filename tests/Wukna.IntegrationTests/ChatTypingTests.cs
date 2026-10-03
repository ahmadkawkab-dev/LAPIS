namespace Wukna.IntegrationTests;

using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wukna.Features.Chat;
using Xunit;
using static ChatControlTestSupport;

public sealed class ChatTypingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Typing_requires_join_and_current_membership_and_is_ephemeral_throttled_and_board_scoped()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        await using var owner = Connection(factory, seed.Owner); await using var guest = Connection(factory, seed.Guest); await using var outsider = Connection(factory, seed.Outsider);
        var events = Listen<ChatTypingEvent>(owner, ChatRealtimeEvents.TypingChanged);
        var leaked = Listen<ChatTypingEvent>(outsider, ChatRealtimeEvents.TypingChanged);
        await Join(owner, seed, ct); await guest.StartAsync(ct); await outsider.StartAsync(ct);
        var beforeJoin = await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("SetTyping", seed.Board.Id, true, ct));
        Assert.Contains("chat_forbidden", beforeJoin.Message, StringComparison.Ordinal);
        var joined = await guest.InvokeAsync<ChatJoinedDto>("JoinBoard", seed.Board.Id, ct);
        await guest.InvokeAsync("SetTyping", seed.Board.Id, true, ct);
        var started = await Read(events, ct);
        Assert.True(started.IsTyping); Assert.Equal(seed.Guest.Id, started.UserId); Assert.Equal(seed.Board.Id, started.BoardId);
        Assert.Equal(joined.MembershipInstanceId, started.MembershipInstanceId); Assert.Equal(seed.Clock.GetUtcNow().AddSeconds(8), started.ExpiresAt);
        Assert.Equal(seed.Guest.Username, started.Sender!.Username);
        await guest.InvokeAsync("SetTyping", seed.Board.Id, true, ct); await None(events, ct);
        seed.Clock.Advance(TimeSpan.FromSeconds(2)); await guest.InvokeAsync("SetTyping", seed.Board.Id, true, ct);
        var renewed = await Read(events, ct); Assert.True(long.Parse(renewed.Sequence) > long.Parse(started.Sequence));
        await guest.InvokeAsync("SetTyping", seed.Board.Id, false, ct); Assert.False((await Read(events, ct)).IsTyping);
        await None(leaked, ct);
        await using var db = postgres.CreateContext(); Assert.False(await db.ChatMessages.AnyAsync(ct)); Assert.False(await db.ChatOutboxEvents.AnyAsync(ct));
    }

    [Fact]
    public async Task Two_tabs_have_separate_leases_and_leave_disconnect_and_mute_stop_typing()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var http = Client(factory, seed.Owner);
        await using var owner = Connection(factory, seed.Owner); await using var first = Connection(factory, seed.Guest); await using var second = Connection(factory, seed.Guest);
        var events = Listen<ChatTypingEvent>(owner, ChatRealtimeEvents.TypingChanged);
        await Join(owner, seed, ct); var joined = await Join(first, seed, ct); await Join(second, seed, ct);
        await first.InvokeAsync("SetTyping", seed.Board.Id, true, ct); var firstStart = await Read(events, ct);
        await second.InvokeAsync("SetTyping", seed.Board.Id, true, ct); var secondStart = await Read(events, ct);
        Assert.NotEqual(firstStart.ConnectionId, secondStart.ConnectionId);
        await first.InvokeAsync("LeaveBoard", seed.Board.Id, ct); var leave = await Read(events, ct);
        Assert.False(leave.IsTyping); Assert.Equal(firstStart.ConnectionId, leave.ConnectionId);
        using var muted = await Mute(http, seed, seed.Guest, joined, true, ct); Assert.Equal(HttpStatusCode.OK, muted.StatusCode);
        await Dispatch(factory, ct); var mute = await Read(events, ct); Assert.False(mute.IsTyping); Assert.Equal(secondStart.ConnectionId, mute.ConnectionId);
        var denied = await Assert.ThrowsAsync<HubException>(() => second.InvokeAsync("SetTyping", seed.Board.Id, true, ct));
        Assert.Contains("chat_muted", denied.Message, StringComparison.Ordinal);
        await second.InvokeAsync("SetTyping", seed.Board.Id, false, ct);
        using var guestHttp = Client(factory, seed.Guest);
        using var unmute = await Mute(http, seed, seed.Guest, await State(guestHttp, seed, ct), false, ct);
        await second.InvokeAsync("SetTyping", seed.Board.Id, true, ct); var resumed = await Read(events, ct); Assert.True(resumed.IsTyping);
        await second.StopAsync(ct); var stopped = await Read(events, ct); Assert.False(stopped.IsTyping); Assert.Equal(secondStart.ConnectionId, stopped.ConnectionId);
        Assert.True(long.Parse(stopped.Sequence) > long.Parse(resumed.Sequence));
    }

    [Fact]
    public async Task Settings_notify_all_members_but_mute_refs_are_private_to_owner_and_target()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var http = Client(factory, seed.Owner);
        await using var owner = Connection(factory, seed.Owner); await using var guest = Connection(factory, seed.Guest); await using var peer = Connection(factory, seed.Peer);
        var ownerSettings = Listen<ChatSettingsChangedEvent>(owner, ChatRealtimeEvents.SettingsChanged);
        var guestSettings = Listen<ChatSettingsChangedEvent>(guest, ChatRealtimeEvents.SettingsChanged);
        var peerSettings = Listen<ChatSettingsChangedEvent>(peer, ChatRealtimeEvents.SettingsChanged);
        var ownerMute = Listen<ChatMemberStateChangedEvent>(owner, ChatRealtimeEvents.MemberStateChanged);
        var guestMute = Listen<ChatMemberStateChangedEvent>(guest, ChatRealtimeEvents.MemberStateChanged);
        var peerMute = Listen<ChatMemberStateChangedEvent>(peer, ChatRealtimeEvents.MemberStateChanged);
        await Join(owner, seed, ct); var joined = await Join(guest, seed, ct); await Join(peer, seed, ct);
        using var settings = await Settings(http, seed, 30, 1, ct); using var mute = await Mute(http, seed, seed.Guest, joined, true, ct);
        await Dispatch(factory, ct);
        Assert.Equal("2", (await Read(ownerSettings, ct)).Revision); Assert.Equal("2", (await Read(guestSettings, ct)).Revision);
        Assert.Equal("2", (await Read(peerSettings, ct)).Revision);
        var ownRef = await Read(ownerMute, ct); var targetRef = await Read(guestMute, ct);
        Assert.Equal(ownRef.EventId, targetRef.EventId); Assert.Equal(seed.Guest.Id, targetRef.MemberUserId);
        Assert.Equal(joined.MembershipInstanceId, targetRef.MembershipInstanceId); Assert.Equal("1", targetRef.Revision);
        await None(peerMute, ct);
    }

    [Fact]
    public async Task Removed_subscription_cannot_send_or_receive_typing_even_when_group_and_registry_are_stale()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        await using var owner = Connection(factory, seed.Owner); await using var guest = Connection(factory, seed.Guest);
        var leaked = Listen<ChatTypingEvent>(guest, ChatRealtimeEvents.TypingChanged);
        await Join(owner, seed, ct); await Join(guest, seed, ct);
        await using (var db = postgres.CreateContext()) await db.BoardMemberships.Where(member => member.BoardId == seed.Board.Id && member.UserId == seed.Guest.Id).ExecuteDeleteAsync(ct);
        await owner.InvokeAsync("SetTyping", seed.Board.Id, true, ct); await None(leaked, ct);
        var denied = await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("SetTyping", seed.Board.Id, true, ct));
        Assert.Contains("chat_forbidden", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delayed_old_mute_and_revocation_cannot_affect_new_membership_or_fresh_unmuted_typing()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var ownerHttp = Client(factory, seed.Owner); using var guestHttp = Client(factory, seed.Guest);
        await using var owner = Connection(factory, seed.Owner); await using var old = Connection(factory, seed.Guest);
        var events = Listen<ChatTypingEvent>(owner, ChatRealtimeEvents.TypingChanged);
        await Join(owner, seed, ct); var before = await Join(old, seed, ct);
        using var mute = await Mute(ownerHttp, seed, seed.Guest, before, true, ct);
        using var remove = await Remove(ownerHttp, seed, seed.Guest, ct); using var invite = await Invite(ownerHttp, seed, seed.Guest, ct);
        await using var fresh = Connection(factory, seed.Guest); var current = await Join(fresh, seed, ct);
        var freshModeration = Listen<ChatMemberStateChangedEvent>(fresh, ChatRealtimeEvents.MemberStateChanged);
        var freshRevocation = Listen<ChatAccessRevokedEvent>(fresh, ChatRealtimeEvents.AccessRevoked);
        Assert.NotEqual(before.MembershipInstanceId, current.MembershipInstanceId);
        await fresh.InvokeAsync("SetTyping", seed.Board.Id, true, ct); Assert.True((await Read(events, ct)).IsTyping);
        await Dispatch(factory, ct); await None(events, ct); await None(freshModeration, ct); await None(freshRevocation, ct);
        using var mutedNew = await Mute(ownerHttp, seed, seed.Guest, current, true, ct);
        using var unmute = await Mute(ownerHttp, seed, seed.Guest, await State(guestHttp, seed, ct), false, ct);
        await Dispatch(factory, ct); await None(events, ct);
        Assert.False((await State(guestHttp, seed, ct)).IsMuted);
    }

    [Fact]
    public async Task Typing_invocation_limit_is_separate_from_group_operations_and_recovers_after_window()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); await using var guest = Connection(factory, seed.Guest);
        await Join(guest, seed, ct);
        for (var index = 0; index < 120; index++) await guest.InvokeAsync("SetTyping", seed.Board.Id, false, ct);
        var denied = await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("SetTyping", seed.Board.Id, true, ct));
        Assert.Contains("chat_typing_rate_limited", denied.Message, StringComparison.Ordinal);
        await guest.InvokeAsync("JoinBoard", seed.Board.Id, ct);
        seed.Clock.Advance(TimeSpan.FromMinutes(1)); await guest.InvokeAsync("SetTyping", seed.Board.Id, true, ct);
    }

    [Fact]
    public void Registry_expires_leases_and_uses_increasing_sequences_for_stop_and_restart()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow); var connections = new ChatConnectionRegistry(clock);
        var user = Guid.NewGuid(); var board = Guid.NewGuid(); connections.Connect("one", user);
        var subscription = new ChatSubscription("one", user, board, Guid.NewGuid()); Assert.True(connections.Subscribe(subscription));
        var typing = new ChatTypingRegistry(connections, clock);
        var first = Assert.IsType<ChatTypingEvent>(typing.Set(subscription, true, new(user, "user", null, null, null)));
        clock.Advance(TimeSpan.FromSeconds(8)); Assert.Null(typing.Set(subscription, false, null));
        var second = Assert.IsType<ChatTypingEvent>(typing.Set(subscription, true, first.Sender));
        Assert.True(long.Parse(second.Sequence) > long.Parse(first.Sequence));
        var stop = Assert.Single(typing.End(connectionId: "one")); Assert.False(stop.IsTyping);
        var third = Assert.IsType<ChatTypingEvent>(typing.Set(subscription, true, first.Sender)); Assert.True(long.Parse(third.Sequence) > long.Parse(stop.Sequence));
        Assert.Empty(typing.End(boardId: Guid.NewGuid()));
    }
}
