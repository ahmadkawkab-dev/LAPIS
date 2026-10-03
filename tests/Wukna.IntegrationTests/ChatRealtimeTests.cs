namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.Chat;
using Wukna.Features.Users;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

public sealed class ChatRealtimeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Chat_hub_requires_auth_membership_and_http_for_durable_sends()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var anonymousHttp = factory.CreateClient();
        await using var anonymous = Connection(factory, null);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync(ct));
        using var queryApi = await anonymousHttp.GetAsync($"{Messages(seed.Board.Id)}?access_token={Token(seed.Owner)}", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, queryApi.StatusCode);
        using var queryHub = await anonymousHttp.PostAsync($"{ChatHub.Path}/negotiate?negotiateVersion=1&access_token={Token(seed.Owner)}", null, ct);
        Assert.Equal(HttpStatusCode.OK, queryHub.StatusCode);
        await using var unrelated = Connection(factory, Token(seed.Outsider));
        await unrelated.StartAsync(ct);
        await ForbiddenJoin(unrelated, seed.Board.Id, ct);
        await using var guest = Connection(factory, Token(seed.Guest));
        await guest.StartAsync(ct);
        var joined = await guest.InvokeAsync<ChatJoinedDto>("JoinBoard", seed.Board.Id, ct);
        Assert.Equal(seed.Board.Id, joined.BoardId);
        Assert.NotEqual(Guid.Empty, joined.MembershipInstanceId);
        Assert.False(joined.IsMuted);
        await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("SendMessage", "No durable hub writes", ct));
        await using var db = postgres.CreateContext();
        Assert.False((await db.BoardMemberships.SingleAsync(member => member.UserId == seed.Guest.Id, ct)).CanEdit);
        Assert.False(await db.ChatMessages.AnyAsync(ct));
    }

    [Fact]
    public async Task Two_authenticated_clients_receive_committed_references_only_for_their_board()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        await using var owner = Connection(factory, Token(seed.Owner), webSockets: true);
        await using var guest = Connection(factory, Token(seed.Guest), webSockets: true);
        await using var outsider = Connection(factory, Token(seed.Outsider));
        var ownerEvents = Listen<ChatMessageCreatedEvent>(owner, ChatRealtimeEvents.MessageCreated);
        var guestEvents = Listen<ChatMessageCreatedEvent>(guest, ChatRealtimeEvents.MessageCreated);
        var outsiderEvents = Listen<ChatMessageCreatedEvent>(outsider, ChatRealtimeEvents.MessageCreated);
        await StartAndJoin(owner, seed.Board.Id, ct);
        await StartAndJoin(guest, seed.Board.Id, ct);
        await StartAndJoin(outsider, seed.OtherBoard.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        var sent = await Send(http, seed.Board.Id, "Private content never enters the event", ct);
        Assert.Equal(1, await Dispatcher(factory).ProcessBatchAsync(ct));
        var first = await Read(ownerEvents, ct);
        var second = await Read(guestEvents, ct);
        Assert.Equal(first, second);
        Assert.Equal(sent.Message.Id, first.MessageId);
        Assert.Equal(sent.Message.Sequence, first.Sequence);
        Assert.Equal(seed.Board.Id, first.BoardId);
        Assert.DoesNotContain("Private content", System.Text.Json.JsonSerializer.Serialize(first), StringComparison.Ordinal);
        await NoEvent(outsiderEvents, ct);
        await using var db = postgres.CreateContext();
        var work = await db.ChatOutboxEvents.SingleAsync(ct);
        Assert.Equal(first.EventId, work.Id);
        Assert.NotNull(work.ProcessedAt);
        Assert.Null(work.LeaseToken);
    }

    [Fact]
    public async Task Hosted_worker_publishes_http_sends_and_acknowledges_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock, polling: true);
        await using var guest = Connection(factory, Token(seed.Guest));
        var events = Listen<ChatMessageCreatedEvent>(guest, ChatRealtimeEvents.MessageCreated);
        await StartAndJoin(guest, seed.Board.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        var sent = await Send(http, seed.Board.Id, "Worker delivery", ct);
        Assert.Equal(sent.Message.Id, (await Read(events, ct)).MessageId);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var db = postgres.CreateContext();
            if (await db.ChatOutboxEvents.AllAsync(work => work.ProcessedAt != null, ct)) return;
            await Task.Delay(50, ct);
        }
        Assert.Fail("Worker did not acknowledge its publication.");
    }

    [Fact]
    public async Task Removal_revokes_connected_access_and_stale_groups_cannot_receive_new_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        await using var guest = Connection(factory, Token(seed.Guest));
        await using var owner = Connection(factory, Token(seed.Owner));
        var revoked = Listen<ChatAccessRevokedEvent>(guest, ChatRealtimeEvents.AccessRevoked);
        var forbidden = Listen<ChatMessageCreatedEvent>(guest, ChatRealtimeEvents.MessageCreated);
        var allowed = Listen<ChatMessageCreatedEvent>(owner, ChatRealtimeEvents.MessageCreated);
        var joined = await StartAndJoin(guest, seed.Board.Id, ct);
        await StartAndJoin(owner, seed.Board.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        using var removal = await http.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        Assert.Equal(joined.MembershipInstanceId, (await Read(revoked, ct)).MembershipInstanceId);
        await ForbiddenJoin(guest, seed.Board.Id, ct);
        await using var reconnect = Connection(factory, Token(seed.Guest));
        await reconnect.StartAsync(ct);
        await ForbiddenJoin(reconnect, seed.Board.Id, ct);

        // Simulate failed eviction: both SignalR group and local subscription are stale.
        await factory.Services.GetRequiredService<IHubContext<ChatHub>>().Groups.AddToGroupAsync(guest.ConnectionId!, ChatHub.Group(seed.Board.Id), ct);
        Assert.True(factory.Services.GetRequiredService<ChatConnectionRegistry>().Subscribe(
            new ChatSubscription(guest.ConnectionId!, seed.Guest.Id, seed.Board.Id, joined.MembershipInstanceId)));
        var sent = await Send(http, seed.Board.Id, "After removal", ct);
        Assert.Equal(2, await Dispatcher(factory).ProcessBatchAsync(ct));
        Assert.Equal(sent.Message.Id, (await Read(allowed, ct)).MessageId);
        await NoEvent(forbidden, ct);
    }

    [Fact]
    public async Task Failed_revocation_excludes_old_instance_and_delayed_retry_preserves_reinvited_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        var faults = new PublicationFaults { FailRevocation = true };
        await using var factory = new ChatFactory(postgres, seed.Clock, faults: faults);
        await using var oldGuest = Connection(factory, Token(seed.Guest));
        var oldMessages = Listen<ChatMessageCreatedEvent>(oldGuest, ChatRealtimeEvents.MessageCreated);
        var oldRevocations = Listen<ChatAccessRevokedEvent>(oldGuest, ChatRealtimeEvents.AccessRevoked);
        var original = await StartAndJoin(oldGuest, seed.Board.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        using var removal = await http.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        await using (var db = postgres.CreateContext())
            await db.ChatOutboxEvents.ExecuteUpdateAsync(set => set.SetProperty(work => work.NextAttemptAt, seed.Clock.GetUtcNow().AddSeconds(30)), ct);
        using var invitation = await http.PutAsJsonAsync($"/api/boards/{seed.Board.Id}/guests",
            new SetGuestAccessRequest(seed.Guest.Email!, false), ct);
        Assert.True(invitation.IsSuccessStatusCode);
        await using var newGuest = Connection(factory, Token(seed.Guest));
        var newMessages = Listen<ChatMessageCreatedEvent>(newGuest, ChatRealtimeEvents.MessageCreated);
        var newRevocations = Listen<ChatAccessRevokedEvent>(newGuest, ChatRealtimeEvents.AccessRevoked);
        var rejoined = await StartAndJoin(newGuest, seed.Board.Id, ct);
        Assert.NotEqual(original.MembershipInstanceId, rejoined.MembershipInstanceId);
        var sent = await Send(http, seed.Board.Id, "Only new membership receives this", ct);
        Assert.Equal(1, await Dispatcher(factory).ProcessBatchAsync(ct));
        Assert.Equal(sent.Message.Id, (await Read(newMessages, ct)).MessageId);
        await NoEvent(oldMessages, ct);
        faults.FailRevocation = false;
        seed.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await Dispatcher(factory).ProcessBatchAsync(ct));
        Assert.Equal(original.MembershipInstanceId, (await Read(oldRevocations, ct)).MembershipInstanceId);
        await NoEvent(newRevocations, ct);
        var retained = Assert.Single(factory.Services.GetRequiredService<ChatConnectionRegistry>().ForBoard(seed.Board.Id));
        Assert.Equal(newGuest.ConnectionId, retained.ConnectionId);
    }

    [Fact]
    public async Task Reconnect_requires_rejoin_and_http_cursor_catch_up_recovers_offline_messages()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guestHttp = Client(factory, seed.Guest, seed.Clock);
        var initial = (await guestHttp.GetFromJsonAsync<ChatHistoryPageDto>(Messages(seed.Board.Id), ct))!;
        await using (var original = Connection(factory, Token(seed.Guest)))
        {
            await StartAndJoin(original, seed.Board.Id, ct);
            await original.StopAsync(ct);
        }
        using var ownerHttp = Client(factory, seed.Owner, seed.Clock);
        for (var index = 0; index < 3; index++) await Send(ownerHttp, seed.Board.Id, $"Offline {index}", ct);
        Assert.Equal(3, await Dispatcher(factory).ProcessBatchAsync(ct));
        await using var reconnect = Connection(factory, Token(seed.Guest));
        var live = Listen<ChatMessageCreatedEvent>(reconnect, ChatRealtimeEvents.MessageCreated);
        await reconnect.StartAsync(ct);
        await Send(ownerHttp, seed.Board.Id, "Before rejoin", ct);
        Assert.Equal(1, await Dispatcher(factory).ProcessBatchAsync(ct));
        await NoEvent(live, ct);
        await reconnect.InvokeAsync<ChatJoinedDto>("JoinBoard", seed.Board.Id, ct);
        var recovered = (await guestHttp.GetFromJsonAsync<ChatHistoryPageDto>($"{Messages(seed.Board.Id)}?after={initial.NewerCursor}", ct))!;
        Assert.Equal(new[] { "1", "2", "3", "4" }, recovered.Items.Select(item => item.Sequence));
        var sent = await Send(ownerHttp, seed.Board.Id, "After rejoin", ct);
        await Dispatcher(factory).ProcessBatchAsync(ct);
        Assert.Equal(sent.Message.Id, (await Read(live, ct)).MessageId);
    }

    [Fact]
    public async Task Publication_failure_keeps_http_success_and_retries_persisted_work_after_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        Guid eventId;
        await using (var failing = new ChatFactory(postgres, seed.Clock, faults: new PublicationFaults { FailMessage = true }))
        {
            using var http = Client(failing, seed.Owner, seed.Clock);
            await Send(http, seed.Board.Id, "Persisted despite transport failure", ct);
            Assert.Equal(0, await Dispatcher(failing).ProcessBatchAsync(ct));
            Assert.Equal(0, await Dispatcher(failing).ProcessBatchAsync(ct));
            await using var db = postgres.CreateContext();
            var work = await db.ChatOutboxEvents.SingleAsync(ct);
            eventId = work.Id;
            Assert.Equal(1, work.Attempts);
            Assert.Null(work.ProcessedAt);
            Assert.Null(work.LeaseToken);
            Assert.Equal(seed.Clock.GetUtcNow().AddSeconds(2), work.NextAttemptAt);
            Assert.Equal("InvalidOperationException", work.LastErrorCode);
            Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        }
        seed.Clock.Advance(TimeSpan.FromSeconds(2));
        await using var restarted = new ChatFactory(postgres, seed.Clock);
        await using var guest = Connection(restarted, Token(seed.Guest));
        var events = Listen<ChatMessageCreatedEvent>(guest, ChatRealtimeEvents.MessageCreated);
        await StartAndJoin(guest, seed.Board.Id, ct);
        Assert.Equal(1, await Dispatcher(restarted).ProcessBatchAsync(ct));
        Assert.Equal(eventId, (await Read(events, ct)).EventId);
        await using var verify = postgres.CreateContext();
        Assert.Equal(2, (await verify.ChatOutboxEvents.SingleAsync(ct)).Attempts);
    }

    [Fact]
    public async Task Failure_after_transport_enqueue_retries_same_event_and_clients_can_deduplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        var faults = new PublicationFaults { FailAfterMessage = true };
        await using var factory = new ChatFactory(postgres, seed.Clock, faults: faults);
        await using var guest = Connection(factory, Token(seed.Guest));
        var events = Listen<ChatMessageCreatedEvent>(guest, ChatRealtimeEvents.MessageCreated);
        await StartAndJoin(guest, seed.Board.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        await Send(http, seed.Board.Id, "Same authoritative message", ct);
        Assert.Equal(0, await Dispatcher(factory).ProcessBatchAsync(ct));
        var first = await Read(events, ct);
        faults.FailAfterMessage = false;
        seed.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await Dispatcher(factory).ProcessBatchAsync(ct));
        var duplicate = await Read(events, ct);
        Assert.Equal(first, duplicate);
        Assert.Single(new[] { first.MessageId, duplicate.MessageId }.Distinct());
        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        Assert.Equal(2, (await db.ChatOutboxEvents.SingleAsync(ct)).Attempts);
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_old_token_cannot_publish_or_acknowledge()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        ChatOutboxEvent abandoned;
        await using (var first = new ChatFactory(postgres, seed.Clock))
        {
            using var http = Client(first, seed.Owner, seed.Clock);
            await Send(http, seed.Board.Id, "Recover abandoned lease", ct);
            await using var db = postgres.CreateContext();
            abandoned = Assert.IsType<ChatOutboxEvent>(await Dispatcher(first).ClaimAsync(db, ct));
            Assert.Null(await Dispatcher(first).ClaimAsync(db, ct));
        }
        seed.Clock.Advance(TimeSpan.FromSeconds(31));
        await using var restarted = new ChatFactory(postgres, seed.Clock);
        using var initialize = Client(restarted, seed.Owner, seed.Clock);
        await using var recoveredDb = postgres.CreateContext();
        var current = Assert.IsType<ChatOutboxEvent>(await Dispatcher(restarted).ClaimAsync(recoveredDb, ct));
        Assert.Equal(abandoned.Id, current.Id);
        Assert.NotEqual(abandoned.LeaseToken, current.LeaseToken);
        Assert.Equal(2, current.Attempts);
        Assert.Equal(0, await ChatOutboxDispatcher.AcknowledgeAsync(recoveredDb, abandoned, seed.Clock.GetUtcNow(), ct));
        Assert.False(await Dispatcher(restarted).DispatchAsync(abandoned, ct));
        Assert.True(await Dispatcher(restarted).DispatchAsync(current, ct));
        Assert.Null(await Dispatcher(restarted).ClaimAsync(recoveredDb, ct));
    }

    [Fact]
    public async Task Concurrent_claimers_claim_each_event_once_and_skip_locked_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var http = Client(factory, seed.Owner, seed.Clock);
        for (var index = 0; index < 6; index++) await Send(http, seed.Board.Id, $"Claim {index}", ct);
        var dispatcher = Dispatcher(factory);
        await using var lockedDb = postgres.CreateContext();
        await using var transaction = await lockedDb.Database.BeginTransactionAsync(ct);
        var locked = Assert.IsType<ChatOutboxEvent>(await dispatcher.ClaimAsync(lockedDb, ct));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = postgres.CreateContext();
            return await dispatcher.ClaimAsync(db, ct);
        }));
        var taken = claims.OfType<ChatOutboxEvent>().ToArray();
        Assert.Equal(5, taken.Length);
        Assert.Equal(5, taken.Select(item => item.Id).Distinct().Count());
        Assert.DoesNotContain(taken, item => item.Id == locked.Id);
        Assert.All(taken, item => Assert.Equal(1, item.Attempts));
        await transaction.RollbackAsync(ct);
        await using var verify = postgres.CreateContext();
        Assert.Equal(locked.Id, (await dispatcher.ClaimAsync(verify, ct))!.Id);
    }

    [Fact]
    public async Task Removal_cannot_commit_while_dispatch_uses_current_recipient_authorization()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = new PublicationFaults { AfterMessage = async () => { entered.TrySetResult(); await release.Task; } };
        await using var factory = new ChatFactory(postgres, seed.Clock, faults: faults);
        await using var guest = Connection(factory, Token(seed.Guest));
        await StartAndJoin(guest, seed.Board.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        await Send(http, seed.Board.Id, "Publication precedes removal", ct);
        var dispatch = Dispatcher(factory).ProcessBatchAsync(ct);
        Task<HttpResponseMessage>? removal = null;
        var removedEarly = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            removal = http.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", ct);
            await Task.Delay(100, ct);
            removedEarly = removal.IsCompleted;
        }
        finally { release.TrySetResult(); }
        await dispatch;
        using var removed = await removal!;
        Assert.False(removedEarly);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await ForbiddenJoin(guest, seed.Board.Id, ct);
    }

    [Fact]
    public async Task Board_deletion_revokes_both_clients_and_surviving_outbox_work_is_drained_safely()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        await using var owner = Connection(factory, Token(seed.Owner));
        await using var guest = Connection(factory, Token(seed.Guest));
        var ownerRevoked = Listen<ChatAccessRevokedEvent>(owner, ChatRealtimeEvents.AccessRevoked);
        var guestRevoked = Listen<ChatAccessRevokedEvent>(guest, ChatRealtimeEvents.AccessRevoked);
        var missingMessage = Listen<ChatMessageCreatedEvent>(guest, ChatRealtimeEvents.MessageCreated);
        await StartAndJoin(owner, seed.Board.Id, ct);
        await StartAndJoin(guest, seed.Board.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        await Send(http, seed.Board.Id, "Pending event when board disappears", ct);
        using var deleted = await http.DeleteAsync($"/api/boards/{seed.Board.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(seed.Board.Id, (await Read(ownerRevoked, ct)).BoardId);
        Assert.Equal(seed.Board.Id, (await Read(guestRevoked, ct)).BoardId);
        Assert.Equal(2, await Dispatcher(factory).ProcessBatchAsync(ct));
        await NoEvent(missingMessage, ct);
        await ForbiddenJoin(owner, seed.Board.Id, ct);
        await using var db = postgres.CreateContext();
        Assert.False(await db.ChatMessages.AnyAsync(ct));
        Assert.Equal(2, await db.ChatOutboxEvents.CountAsync(work => work.ProcessedAt != null, ct));
    }

    [Fact]
    public async Task Leaving_unsubscribes_and_group_operations_have_a_bounded_connection_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        await using var guest = Connection(factory, Token(seed.Guest));
        var messages = Listen<ChatMessageCreatedEvent>(guest, ChatRealtimeEvents.MessageCreated);
        await StartAndJoin(guest, seed.Board.Id, ct);
        for (var index = 0; index < 59; index++) await guest.InvokeAsync("LeaveBoard", seed.Board.Id, ct);
        Assert.Empty(factory.Services.GetRequiredService<ChatConnectionRegistry>().ForBoard(seed.Board.Id));
        var denied = await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("JoinBoard", seed.Board.Id, ct));
        Assert.Contains("chat_hub_rate_limited", denied.Message, StringComparison.Ordinal);
        using var http = Client(factory, seed.Owner, seed.Clock);
        await Send(http, seed.Board.Id, "While unsubscribed", ct);
        await Dispatcher(factory).ProcessBatchAsync(ct);
        await NoEvent(messages, ct);
        seed.Clock.Advance(TimeSpan.FromMinutes(1));
        await guest.InvokeAsync("JoinBoard", seed.Board.Id, ct);
    }

    [Fact]
    public async Task Publication_timeout_rolls_back_acknowledgement_and_reschedules_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = new PublicationFaults { AfterMessage = () => stalled.Task };
        await using var factory = new ChatFactory(postgres, seed.Clock, faults: faults, publicationTimeout: 1);
        using var http = Client(factory, seed.Owner, seed.Clock);
        await Send(http, seed.Board.Id, "Retry bounded transport work", ct);
        Assert.Equal(0, await Dispatcher(factory).ProcessBatchAsync(ct));
        await using var db = postgres.CreateContext();
        var work = await db.ChatOutboxEvents.SingleAsync(ct);
        Assert.Null(work.ProcessedAt);
        Assert.Null(work.LeaseToken);
        Assert.Equal("publication_timeout", work.LastErrorCode);
        faults.AfterMessage = null;
        seed.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await Dispatcher(factory).ProcessBatchAsync(ct));
    }

    [Fact]
    public async Task Failed_removal_rolls_back_membership_state_and_revocation_event_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock, interceptor: new FailAfterRevocationWrite());
        await using var guest = Connection(factory, Token(seed.Guest));
        var revocations = Listen<ChatAccessRevokedEvent>(guest, ChatRealtimeEvents.AccessRevoked);
        var joined = await StartAndJoin(guest, seed.Board.Id, ct);
        using var http = Client(factory, seed.Owner, seed.Clock);
        using var failed = await http.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", ct);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await NoEvent(revocations, ct);
        await using var db = postgres.CreateContext();
        Assert.True(await db.BoardMemberships.AnyAsync(member => member.BoardId == seed.Board.Id && member.UserId == seed.Guest.Id, ct));
        Assert.Equal(joined.MembershipInstanceId, (await db.BoardMemberChatStates.SingleAsync(ct)).MembershipInstanceId);
        Assert.False(await db.ChatOutboxEvents.AnyAsync(ct));
        await guest.InvokeAsync("JoinBoard", seed.Board.Id, ct);
    }

    private async Task<SeedData> Seed(CancellationToken ct)
    {
        await postgres.ResetAsync(ct);
        var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var owner = User("owner");
        var guest = User("guest");
        var outsider = User("outsider");
        var board = new Board { Title = "Chat realtime" };
        board.Memberships.Add(new BoardMembership { User = owner, Role = BoardRole.Owner, CanEdit = true });
        board.Memberships.Add(new BoardMembership { User = guest, Role = BoardRole.Guest, CanEdit = false });
        var other = new Board { Title = "Other board" };
        other.Memberships.Add(new BoardMembership { User = outsider, Role = BoardRole.Owner, CanEdit = true });
        await using var db = postgres.CreateContext();
        db.Boards.AddRange(board, other);
        await db.SaveChangesAsync(ct);
        return new SeedData(board, other, owner, guest, outsider, clock);
    }

    private static User User(string prefix) => new() { Email = prefix + "@chat-realtime.test", NormalizedEmail = prefix.ToUpperInvariant() + "@CHAT-REALTIME.TEST" };
    private static string Messages(Guid boardId) => $"/api/boards/{boardId}/chat/messages";
    private static ChatOutboxDispatcher Dispatcher(WuknaWebApplicationFactory factory) => factory.Services.GetRequiredService<ChatOutboxDispatcher>();
    // JWT lifetime validation uses real time; only chat business time is simulated.
    private static string Token(User user) => new JwtTokenGenerator(new JwtOptions
    {
        Issuer = WuknaWebApplicationFactory.JwtIssuer, Audience = WuknaWebApplicationFactory.JwtAudience,
        SigningKey = WuknaWebApplicationFactory.JwtSigningKey, AccessTokenMinutes = 60
    }, TimeProvider.System).CreateAccessToken(user).Token;
    private static HttpClient Client(WuknaWebApplicationFactory factory, User user, TimeProvider clock)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        return client;
    }
    private static HubConnection Connection(WuknaWebApplicationFactory factory, string? token, bool webSockets = false)
    {
        _ = factory.Server;
        return new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, ChatHub.Path), options =>
        {
            options.Transports = webSockets ? HttpTransportType.WebSockets : HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            if (webSockets)
            {
                options.SkipNegotiation = true;
                // Exercise browser-style query-token authentication with TestServer's
                // actual WebSocket transport instead of an Authorization header.
                options.WebSocketFactory = async (context, ct) =>
                {
                    var uri = token is null ? context.Uri : new Uri(context.Uri +
                        (string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&") + "access_token=" + Uri.EscapeDataString(token));
                    return await factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct);
                };
            }
        }).Build();
    }
    private static async Task<ChatJoinedDto> StartAndJoin(HubConnection connection, Guid boardId, CancellationToken ct)
    {
        await connection.StartAsync(ct);
        return await connection.InvokeAsync<ChatJoinedDto>("JoinBoard", boardId, ct);
    }
    private static async Task ForbiddenJoin(HubConnection connection, Guid boardId, CancellationToken ct)
    {
        var exception = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinBoard", boardId, ct));
        Assert.Contains("chat_forbidden", exception.Message, StringComparison.Ordinal);
    }
    private static Channel<T> Listen<T>(HubConnection connection, string eventName)
    {
        var channel = Channel.CreateUnbounded<T>();
        connection.On<T>(eventName, item => { channel.Writer.TryWrite(item); });
        return channel;
    }
    private static async Task<T> Read<T>(Channel<T> channel, CancellationToken ct) =>
        await channel.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
    private static async Task NoEvent<T>(Channel<T> channel, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(250);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.Reader.ReadAsync(deadline.Token).AsTask());
    }
    private static async Task<ChatSendResultDto> Send(HttpClient http, Guid boardId, string body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Messages(boardId), new SendChatMessageRequest(Guid.NewGuid(), body), ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<ChatSendResultDto>(await response.Content.ReadFromJsonAsync<ChatSendResultDto>(ct));
    }
    private sealed record SeedData(Board Board, Board OtherBoard, User Owner, User Guest, User Outsider, ManualTimeProvider Clock);
    private sealed class PublicationFaults
    {
        public bool FailMessage { get; set; }
        public bool FailRevocation { get; set; }
        public bool FailAfterMessage { get; set; }
        public Func<Task>? AfterMessage { get; set; }
    }
    private sealed class FaultPublisher(ChatRealtimePublisher inner, PublicationFaults faults) : IChatRealtimePublisher
    {
        public async Task PublishAsync(WuknaDbContext db, ChatOutboxEvent notification, CancellationToken ct)
        {
            if (notification.Kind == ChatOutboxEventKind.AccessRevoked && faults.FailRevocation ||
                notification.Kind == ChatOutboxEventKind.MessageCreated && faults.FailMessage)
                throw new InvalidOperationException("Simulated transport failure.");
            await inner.PublishAsync(db, notification, ct);
            if (notification.Kind == ChatOutboxEventKind.MessageCreated)
            {
                if (faults.AfterMessage is not null) await faults.AfterMessage().WaitAsync(ct);
                if (faults.FailAfterMessage) throw new InvalidOperationException("Simulated crash before acknowledgement.");
            }
        }
    }
    private sealed class ChatFactory : WuknaWebApplicationFactory
    {
        private readonly PostgresFixture postgres;
        private readonly bool polling;
        private readonly PublicationFaults? faults;
        private readonly int publicationTimeout;
        private readonly IInterceptor? interceptor;
        public ChatFactory(PostgresFixture postgres, ManualTimeProvider clock, bool polling = false,
            PublicationFaults? faults = null, int publicationTimeout = 5, IInterceptor? interceptor = null)
            : base(postgres, clock)
        {
            this.polling = polling; this.faults = faults; this.publicationTimeout = publicationTimeout;
            this.postgres = postgres; this.interceptor = interceptor;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<ChatOutboxOptions>(options =>
                {
                    options.Enabled = polling; options.PollMilliseconds = 100;
                    options.PublicationTimeoutSeconds = publicationTimeout;
                });
                if (interceptor is not null)
                {
                    services.RemoveAll<WuknaDbContext>();
                    services.RemoveAll<DbContextOptions<WuknaDbContext>>();
                    services.AddDbContext<WuknaDbContext>(options => options.UseNpgsql(postgres.ConnectionString)
                        .UseSnakeCaseNamingConvention().AddInterceptors(interceptor));
                }
                if (faults is null) return;
                services.RemoveAll<IChatRealtimePublisher>();
                services.AddSingleton<IChatRealtimePublisher>(provider => new FaultPublisher(new ChatRealtimePublisher(
                    provider.GetRequiredService<IHubContext<ChatHub>>(), provider.GetRequiredService<ChatConnectionRegistry>(),
                    provider.GetRequiredService<ChatTypingService>(), provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<ChatRealtimePublisher>>()), faults));
            });
        }
    }
    private sealed class FailAfterRevocationWrite : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ChatOutboxEvent>()
                .Any(entry => entry.Entity.Kind == ChatOutboxEventKind.AccessRevoked))
                throw new InvalidOperationException("Simulated failure after membership and outbox writes.");
            return ValueTask.FromResult(result);
        }
    }
}
