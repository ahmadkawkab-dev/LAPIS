namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wukna.Features.Board;
using Xunit;
using static ChatControlTestSupport;

public sealed class BoardGuestLimitTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Default_allows_twenty_guests_plus_owner_and_concurrent_invitations_cannot_exceed_cap()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct, guests: false);
        var users = Enumerable.Range(0, 24).Select(index => User($"invite-{index}")).ToArray();
        await using (var db = postgres.CreateContext()) { db.Users.AddRange(users); await db.SaveChangesAsync(ct); }
        await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner);
        var replies = await Task.WhenAll(users.Select(user => Invite(owner, seed, user, ct)));
        try {
            Assert.Equal(20, replies.Count(reply => reply.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(4, replies.Count(reply => reply.StatusCode == HttpStatusCode.Conflict));
            foreach (var denied in replies.Where(reply => reply.StatusCode == HttpStatusCode.Conflict))
                Assert.Contains("board_guest_limit_reached", await denied.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        } finally { foreach (var reply in replies) reply.Dispose(); }
        await using var verify = postgres.CreateContext(); Assert.Equal(21, await verify.BoardMemberships.CountAsync(ct));
        using var limit = await owner.GetAsync($"/api/boards/{seed.Board.Id}/guest-limit", ct);
        Assert.Equal("no-store", limit.Headers.CacheControl?.ToString());
        Assert.Equal(new GuestLimit(20, 20), await limit.Content.ReadFromJsonAsync<GuestLimit>(ct));
        using var existing = await Invite(owner, seed, users.First(user => verify.BoardMemberships.Any(member => member.UserId == user.Id)), ct, edit: true);
        Assert.Equal(HttpStatusCode.NoContent, existing.StatusCode);
    }

    [Fact]
    public async Task Same_guest_concurrent_invites_are_idempotent_and_permission_changes_work_at_capacity()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct, guests: false);
        await using var factory = new Factory(postgres, seed.Clock) { MaxGuests = 1 }; using var owner = Client(factory, seed.Owner);
        var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Invite(owner, seed, seed.Guest, ct)));
        try { Assert.All(replies, reply => Assert.Equal(HttpStatusCode.NoContent, reply.StatusCode)); }
        finally { foreach (var reply in replies) reply.Dispose(); }
        using var update = await Invite(owner, seed, seed.Guest, ct, edit: true); Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using var patch = await owner.PatchAsJsonAsync($"/api/boards/{seed.Board.Id}/members/{seed.Guest.Id}", new SetMemberPermissionRequest(false), ct);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        using var full = await Invite(owner, seed, seed.Peer, ct); Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        await using var db = postgres.CreateContext(); Assert.Equal(2, await db.BoardMemberships.CountAsync(ct));
        Assert.False((await db.BoardMemberships.SingleAsync(member => member.UserId == seed.Guest.Id, ct)).CanEdit);
    }

    [Fact]
    public async Task Lowering_configuration_keeps_members_and_rejects_invites_until_below_limit_including_zero()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner);
        _ = factory.Services;
        factory.MaxGuests = 1;
        factory.Services.GetRequiredService<IOptionsMonitorCache<BoardOptions>>().TryRemove(Options.DefaultName);
        using var denied = await Invite(owner, seed, seed.Outsider, ct); Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        await using (var db = postgres.CreateContext()) Assert.Equal(3, await db.BoardMemberships.CountAsync(ct));
        using var updated = await Invite(owner, seed, seed.Guest, ct, edit: true); Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var removeFirst = await Remove(owner, seed, seed.Guest, ct);
        using var stillFull = await Invite(owner, seed, seed.Outsider, ct); Assert.Equal(HttpStatusCode.Conflict, stillFull.StatusCode);
        using var removeSecond = await Remove(owner, seed, seed.Peer, ct);
        using var nowAllowed = await Invite(owner, seed, seed.Outsider, ct); Assert.Equal(HttpStatusCode.NoContent, nowAllowed.StatusCode);
        factory.MaxGuests = 0; factory.Services.GetRequiredService<IOptionsMonitorCache<BoardOptions>>().TryRemove(Options.DefaultName);
        using var zero = await Invite(owner, seed, seed.Guest, ct); Assert.Equal(HttpStatusCode.Conflict, zero.StatusCode);
        await using var verify = postgres.CreateContext(); Assert.Equal(2, await verify.BoardMemberships.CountAsync(ct));
    }

    [Fact]
    public async Task Guest_limits_are_members_only_and_membership_writes_have_separate_identity_rate_limit()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock) { MembershipRate = 1, ModerationRate = 1 };
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest); using var outsider = Client(factory, seed.Outsider);
        using var outsiderLimit = await outsider.GetAsync($"/api/boards/{seed.Board.Id}/guest-limit", ct);
        Assert.Equal(HttpStatusCode.NotFound, outsiderLimit.StatusCode);
        using var guestLimit = await guest.GetAsync($"/api/boards/{seed.Board.Id}/guest-limit", ct); Assert.Equal(HttpStatusCode.OK, guestLimit.StatusCode);
        using var guestInvite = await Invite(guest, seed, seed.Outsider, ct); Assert.Equal(HttpStatusCode.Forbidden, guestInvite.StatusCode);
        using var invite = await Invite(owner, seed, seed.Outsider, ct); Assert.Equal(HttpStatusCode.NoContent, invite.StatusCode);
        using var membershipLimited = await Remove(owner, seed, seed.Guest, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, membershipLimited.StatusCode);
        Assert.Contains("board_membership_rate_limited", await membershipLimited.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.NotNull(membershipLimited.Headers.RetryAfter);
        using var settings = await Settings(owner, seed, 10, 1, ct); Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        using var moderationLimited = await Settings(owner, seed, 20, 2, ct); Assert.Equal(HttpStatusCode.TooManyRequests, moderationLimited.StatusCode);
        Assert.Contains("chat_moderation_rate_limited", await moderationLimited.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        using var message = await Send(owner, seed, ct); Assert.Equal(HttpStatusCode.Created, message.StatusCode);
    }

    [Fact]
    public async Task Removal_invitation_and_chat_send_complete_without_lock_cycle_or_excess_guests()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock) { MaxGuests = 2 }; using var owner = Client(factory, seed.Owner); using var peer = Client(factory, seed.Peer);
        var replies = await Task.WhenAll(Remove(owner, seed, seed.Guest, ct), Invite(owner, seed, seed.Outsider, ct), Send(peer, seed, ct)).WaitAsync(TimeSpan.FromSeconds(10), ct);
        try {
            Assert.Equal(HttpStatusCode.NoContent, replies[0].StatusCode);
            Assert.Contains(replies[1].StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
            Assert.Equal(HttpStatusCode.Created, replies[2].StatusCode);
        } finally { foreach (var reply in replies) reply.Dispose(); }
        await using var db = postgres.CreateContext(); Assert.InRange(await db.BoardMemberships.CountAsync(member => member.Role == BoardRole.Guest, ct), 1, 2);
    }
    private sealed record GuestLimit(int MaxGuests, int GuestCount);
}
