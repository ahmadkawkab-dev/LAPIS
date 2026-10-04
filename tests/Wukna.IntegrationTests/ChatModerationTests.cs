namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wukna.Features.Chat;
using Xunit;
using static ChatControlTestSupport;

public sealed class ChatModerationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Only_owner_can_moderate_and_owner_cannot_be_muted()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        using var outsider = Client(factory, seed.Outsider); using var anonymous = factory.CreateClient();
        var guestState = await State(guest, seed, ct); var ownerState = await State(owner, seed, ct);
        Assert.False(guestState.IsOwner); Assert.True(ownerState.IsOwner);
        using var guestSettings = await Settings(guest, seed, 30, 1, ct);
        using var guestMute = await Mute(guest, seed, seed.Peer, guestState, true, ct);
        using var guestMembers = await guest.GetAsync(Path(seed) + "/members", ct);
        using var outsiderSettings = await Settings(outsider, seed, 30, 1, ct);
        using var outsiderState = await outsider.GetAsync(Path(seed) + "/state", ct);
        using var outsiderMembers = await outsider.GetAsync(Path(seed) + "/members", ct);
        using var noAuth = await anonymous.GetAsync(Path(seed) + "/state", ct);
        using var protectedOwner = await Mute(owner, seed, seed.Owner, ownerState, true, ct);
        using var missingTarget = await Mute(owner, seed, seed.Outsider, guestState, true, ct);
        Assert.Equal(HttpStatusCode.Forbidden, guestSettings.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, guestMute.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, guestMembers.StatusCode); Assert.Equal(HttpStatusCode.NotFound, outsiderSettings.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderState.StatusCode); Assert.Equal(HttpStatusCode.NotFound, outsiderMembers.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode); Assert.Equal(HttpStatusCode.Conflict, protectedOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTarget.StatusCode);
        await using var db = postgres.CreateContext(); Assert.False(await db.ChatOutboxEvents.AnyAsync(ct));
    }

    [Fact]
    public async Task Mute_persists_allows_read_and_replay_and_unmute_restores_send_without_edit_permission()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        var operation = Guid.NewGuid(); using var sent = await Send(guest, seed, ct, operation);
        var state = await State(guest, seed, ct);
        using var muted = await Mute(owner, seed, seed.Guest, state, true, ct);
        Assert.Equal(HttpStatusCode.OK, muted.StatusCode);
        using var duplicate = await Mute(owner, seed, seed.Guest, state, true, ct);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using var denied = await Send(guest, seed, ct); using var history = await guest.GetAsync(Path(seed) + "/messages", ct);
        using var replay = await Send(guest, seed, ct, operation);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var muteError = await denied.Content.ReadFromJsonAsync<MuteError>(ct);
        Assert.Equal(state.MembershipInstanceId, muteError!.MembershipInstanceId);
        Assert.Equal(1, muteError.ModerationRevision);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using (var restarted = new Factory(postgres, seed.Clock))
        {
            using var reload = Client(restarted, seed.Guest); var persisted = await State(reload, seed, ct);
            Assert.True(persisted.IsMuted); Assert.Null(persisted.MutedUntil); Assert.Equal(1, persisted.ModerationRevision);
        }
        using var staleUnmute = await Mute(owner, seed, seed.Guest, state, false, ct);
        Assert.Equal(HttpStatusCode.Conflict, staleUnmute.StatusCode);
        using var unmuted = await Mute(owner, seed, seed.Guest, await State(guest, seed, ct), false, ct);
        Assert.Equal(HttpStatusCode.OK, unmuted.StatusCode);
        using var allowed = await Send(guest, seed, ct); Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.ChatOutboxEvents.CountAsync(item => item.Kind == ChatOutboxEventKind.MemberStateChanged, ct));
        Assert.Equal(seed.Board.UpdatedAt, (await db.Boards.SingleAsync(ct)).UpdatedAt);
        Assert.False((await db.BoardMemberships.SingleAsync(member => member.UserId == seed.Guest.Id, ct)).CanEdit);
    }

    [Fact]
    public async Task Timed_mute_expires_using_server_time_and_invalid_commands_leave_state_unchanged()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        var state = await State(guest, seed, ct);
        using var past = await Mute(owner, seed, seed.Guest, state, true, ct, seed.Clock.GetUtcNow());
        using var invalidFalse = await Mute(owner, seed, seed.Guest, state, false, ct, seed.Clock.GetUtcNow().AddMinutes(1));
        using var badSettings = await Settings(owner, seed, 21601, 1, ct);
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, invalidFalse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSettings.StatusCode);
        using var mute = await Mute(owner, seed, seed.Guest, state, true, ct, seed.Clock.GetUtcNow().AddMinutes(1));
        Assert.Equal(HttpStatusCode.OK, mute.StatusCode);
        using var denied = await Send(guest, seed, ct); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        seed.Clock.Advance(TimeSpan.FromMinutes(1)); Assert.False((await State(guest, seed, ct)).IsMuted);
        using var allowed = await Send(guest, seed, ct); Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Settings_revisions_are_replay_safe_reset_old_cooldowns_and_keep_owner_exempt()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest); using var peer = Client(factory, seed.Peer);
        using var settings = await Settings(owner, seed, 30, 1, ct);
        var changed = Assert.IsType<ChatSettingsDto>(await settings.Content.ReadFromJsonAsync<ChatSettingsDto>(ct)); Assert.Equal(2, changed.SettingsRevision);
        using var replay = await Settings(owner, seed, 30, 1, ct); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var conflict = await Settings(owner, seed, 60, 1, ct); Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var first = await Send(guest, seed, ct); using var denied = await Send(guest, seed, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal(seed.Clock.GetUtcNow().AddSeconds(30), (await State(guest, seed, ct)).NextSendAllowedAt);
        using var differentUser = await Send(peer, seed, ct); Assert.Equal(HttpStatusCode.Created, differentUser.StatusCode);
        using var ownerFirst = await Send(owner, seed, ct); using var ownerSecond = await Send(owner, seed, ct);
        Assert.Equal(HttpStatusCode.Created, ownerFirst.StatusCode); Assert.Equal(HttpStatusCode.Created, ownerSecond.StatusCode);
        using var shorter = await Settings(owner, seed, 10, 2, ct); Assert.Equal(HttpStatusCode.OK, shorter.StatusCode);
        Assert.Null((await State(guest, seed, ct)).NextSendAllowedAt);
        using var reset = await Send(guest, seed, ct); Assert.Equal(HttpStatusCode.Created, reset.StatusCode);
        using var normal = await Settings(owner, seed, 0, 3, ct); Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        using var now = await Send(guest, seed, ct); using var again = await Send(guest, seed, ct);
        Assert.Equal(HttpStatusCode.Created, now.StatusCode); Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(3, await db.ChatOutboxEvents.CountAsync(item => item.Kind == ChatOutboxEventKind.SettingsChanged, ct));
        Assert.Equal(seed.Board.UpdatedAt, (await db.Boards.SingleAsync(ct)).UpdatedAt);
    }

    [Fact]
    public async Task Stale_membership_instance_cannot_mute_a_reinvited_member()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        var before = await State(guest, seed, ct);
        using var removed = await Remove(owner, seed, seed.Guest, ct); Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var invited = await Invite(owner, seed, seed.Guest, ct); Assert.Equal(HttpStatusCode.NoContent, invited.StatusCode);
        using var denied = await Mute(owner, seed, seed.Guest, before, true, ct);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var current = await State(guest, seed, ct); Assert.NotEqual(before.MembershipInstanceId, current.MembershipInstanceId); Assert.False(current.IsMuted);
        Assert.Equal(0, current.ModerationRevision);
    }

    [Fact]
    public async Task Moderation_list_has_stable_instances_bounded_paging_and_no_email_addresses()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using (var db = postgres.CreateContext())
        {
            for (var index = 0; index < 105; index++) db.BoardMemberships.Add(new() { BoardId = seed.Board.Id, User = User($"legacy-{index}") });
            await db.SaveChangesAsync(ct);
        }
        await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner);
        using var response = await owner.GetAsync(Path(seed) + "/members", ct);
        Assert.DoesNotContain("email", await response.Content.ReadAsStringAsync(ct), StringComparison.OrdinalIgnoreCase);
        var first = Assert.IsType<ChatMemberPageDto>(await response.Content.ReadFromJsonAsync<ChatMemberPageDto>(ct));
        Assert.Equal(100, first.Items.Count); Assert.NotNull(first.NextUserId);
        var last = Assert.IsType<ChatMemberPageDto>(await owner.GetFromJsonAsync<ChatMemberPageDto>(Path(seed) + $"/members?afterUserId={first.NextUserId}", ct));
        Assert.Equal(8, last.Items.Count); Assert.Null(last.NextUserId);
        var reload = Assert.IsType<ChatMemberPageDto>(await owner.GetFromJsonAsync<ChatMemberPageDto>(Path(seed) + "/members", ct));
        Assert.Equal(first.Items.Select(item => item.MembershipInstanceId), reload.Items.Select(item => item.MembershipInstanceId));
        Assert.Equal(108, first.Items.Concat(last.Items).Select(item => item.Sender.UserId).Distinct().Count());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Domain_change_and_outbox_roll_back_together_on_write_failure(bool mute)
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        ChatJoinedDto before;
        await using (var normal = new Factory(postgres, seed.Clock)) { using var guest = Client(normal, seed.Guest); before = await State(guest, seed, ct); }
        await using var factory = new Factory(postgres, seed.Clock, new FailModerationSave()); using var owner = Client(factory, seed.Owner);
        using var failure = mute ? await Mute(owner, seed, seed.Guest, before, true, ct) : await Settings(owner, seed, 30, 1, ct);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.False(await db.ChatOutboxEvents.AnyAsync(ct)); Assert.Equal(0, (await db.BoardChatSettings.SingleAsync(ct)).SlowModeSeconds);
        Assert.False((await db.BoardMemberChatStates.SingleAsync(ct)).IsMuted);
    }
    private sealed class FailModerationSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ChatOutboxEvent>().Any()) throw new InvalidOperationException("Simulated crash after domain and outbox writes");
            return ValueTask.FromResult(result);
        }
    }
    private sealed record MuteError(Guid MembershipInstanceId, long ModerationRevision);
}
