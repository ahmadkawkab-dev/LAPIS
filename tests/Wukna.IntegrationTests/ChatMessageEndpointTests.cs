namespace Wukna.IntegrationTests;

using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.Chat;
using Wukna.Features.Users;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

public sealed class ChatMessageEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Owner_and_read_only_guest_send_authoritative_text_with_profiles_and_outbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        using var first = await Send(owner, seed.Board.Id, "Owner", ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var second = await guest.PostAsJsonAsync(Path(seed.Board.Id), new
        {
            clientMessageId = Guid.NewGuid(), body = "<script>alert('plain text')</script>", senderUserId = seed.Owner.Id
        }, ct);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var sent = await Result(second, ct);
        Assert.Equal(seed.Guest.Id, sent.Message.Sender.UserId);
        Assert.Equal(seed.Guest.Username, sent.Message.Sender.Username);
        Assert.Equal("Guest display", sent.Message.Sender.DisplayName);
        Assert.Equal("<script>alert('plain text')</script>", sent.Message.Body);
        Assert.Equal(seed.Clock.GetUtcNow(), sent.Message.CreatedAt);
        Assert.Equal("2", sent.Message.Sequence);
        var page = await Page(owner, seed.Board.Id, "", ct);
        Assert.Equal(new[] { "1", "2" }, page.Items.Select(message => message.Sequence));
        using var reloaded = await guest.GetAsync($"{Path(seed.Board.Id)}/{sent.Message.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        Assert.Equal("no-store", reloaded.Headers.CacheControl?.ToString());
        await using var db = postgres.CreateContext();
        var events = await db.ChatOutboxEvents.OrderBy(item => item.MessageSequence).ToListAsync(ct);
        Assert.Equal(2, events.Count);
        Assert.All(events, item =>
        {
            Assert.Equal(ChatOutboxEventKind.MessageCreated, item.Kind);
            Assert.Null(item.ProcessedAt);
        });
        Assert.False((await db.BoardMemberships.SingleAsync(member => member.UserId == seed.Guest.Id, ct)).CanEdit);
        Assert.Equal(seed.Board.UpdatedAt, (await db.Boards.SingleAsync(ct)).UpdatedAt);
    }

    [Fact]
    public async Task Anonymous_unrelated_wrong_board_and_removed_users_cannot_read_or_send()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        using var outsider = Client(factory, seed.OtherGuest, seed.Clock);
        using var anonymous = factory.CreateClient();
        using var created = await Send(owner, seed.Board.Id, "Private", ct);
        var message = (await Result(created, ct)).Message;
        await using (var db = postgres.CreateContext())
        {
            await db.BoardMemberships.Where(member => member.UserId == seed.OtherGuest.Id).ExecuteDeleteAsync(ct);
            var otherBoard = new Board { Title = "Unrelated" };
            otherBoard.Memberships.Add(new BoardMembership { UserId = seed.OtherGuest.Id, Role = BoardRole.Owner, CanEdit = true });
            db.Boards.Add(otherBoard);
            await db.SaveChangesAsync(ct);
            using var wrongBoard = await outsider.GetAsync($"{Path(otherBoard.Id)}/{message.Id}", ct);
            Assert.Equal(HttpStatusCode.NotFound, wrongBoard.StatusCode);
        }
        using var unauthenticated = await anonymous.GetAsync(Path(seed.Board.Id), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var deniedRead = await outsider.GetAsync(Path(seed.Board.Id), ct);
        using var deniedSend = await Send(outsider, seed.Board.Id, "Forbidden", ct);
        Assert.Equal(HttpStatusCode.NotFound, deniedRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedSend.StatusCode);
        using var removed = await owner.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var removedRead = await guest.GetAsync(Path(seed.Board.Id), ct);
        using var removedSingle = await guest.GetAsync($"{Path(seed.Board.Id)}/{message.Id}", ct);
        using var removedSend = await Send(guest, seed.Board.Id, "Forbidden after removal", ct);
        Assert.Equal(HttpStatusCode.NotFound, removedRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedSingle.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedSend.StatusCode);
        await using var verify = postgres.CreateContext();
        Assert.Equal(1, await verify.ChatMessages.CountAsync(ct));
    }

    [Fact]
    public async Task Concurrent_identical_retries_create_one_message_and_one_outbox_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct, slowModeSeconds: 30);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        var operation = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Send(guest, seed.Board.Id, "Same operation", ct, operation)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            var results = await Task.WhenAll(responses.Select(response => Result(response, ct)));
            Assert.Single(results.Select(result => result.Message.Id).Distinct());
            Assert.All(results, result => Assert.Equal(seed.Clock.GetUtcNow().AddSeconds(30), result.NextSendAllowedAt));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        Assert.Equal(1, await db.ChatOutboxEvents.CountAsync(ct));
        Assert.Equal(1, (await db.BoardChatSettings.SingleAsync(ct)).LastMessageSequence);
    }

    [Fact]
    public async Task Operation_id_reuse_with_changed_content_conflicts_without_consuming_another_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        var operation = Guid.NewGuid();
        using var created = await Send(guest, seed.Board.Id, "Original", ct, operation);
        using var changed = await Send(guest, seed.Board.Id, "Changed", ct, operation);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("chat_operation_conflict", await Error(changed, ct));
        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        Assert.Equal(1, await db.ChatOutboxEvents.CountAsync(ct));
    }

    [Fact]
    public async Task Mute_blocks_new_sends_but_allows_reads_and_replaying_existing_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        var operation = Guid.NewGuid();
        using var original = await Send(guest, seed.Board.Id, "Already committed", ct, operation);
        await using (var db = postgres.CreateContext())
            await db.BoardMemberChatStates.Where(state => state.UserId == seed.Guest.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(state => state.IsMuted, true), ct);
        using var denied = await Send(guest, seed.Board.Id, "Muted", ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("chat_muted", await Error(denied, ct));
        using var replay = await Send(guest, seed.Board.Id, "Already committed", ct, operation);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await Result(replay, ct)).IsReplay);
        Assert.Single((await Page(guest, seed.Board.Id, "", ct)).Items);
        await using (var db = postgres.CreateContext())
            await db.BoardMemberChatStates.Where(state => state.UserId == seed.Guest.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(state => state.MutedUntil, seed.Clock.GetUtcNow().AddSeconds(-1)), ct);
        using var expiredMute = await Send(guest, seed.Board.Id, "Mute expired", ct);
        Assert.Equal(HttpStatusCode.Created, expiredMute.StatusCode);
    }

    [Fact]
    public async Task Slow_mode_is_per_member_owner_exempt_and_expiry_uses_server_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct, slowModeSeconds: 30);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        using var otherGuest = Client(factory, seed.OtherGuest, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var first = await Send(guest, seed.Board.Id, "First", ct);
        using var blocked = await Send(guest, seed.Board.Id, "Cooldown", ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("chat_cooldown", await Error(blocked, ct));
        Assert.Equal(TimeSpan.FromSeconds(30), blocked.Headers.RetryAfter?.Delta);
        using var independent = await Send(otherGuest, seed.Board.Id, "Independent", ct);
        using var ownerFirst = await Send(owner, seed.Board.Id, "Owner first", ct);
        using var ownerSecond = await Send(owner, seed.Board.Id, "Owner second", ct);
        Assert.Equal(HttpStatusCode.Created, independent.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ownerFirst.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ownerSecond.StatusCode);
        Assert.Null((await Result(ownerSecond, ct)).NextSendAllowedAt);
        seed.Clock.Advance(TimeSpan.FromSeconds(30));
        using var eligible = await Send(guest, seed.Board.Id, "At expiry", ct);
        Assert.Equal(HttpStatusCode.Created, eligible.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(5, await db.ChatMessages.CountAsync(ct));
    }

    [Fact]
    public async Task Concurrent_distinct_sends_cannot_bypass_member_cooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct, slowModeSeconds: 30);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var firstClient = Client(factory, seed.Guest, seed.Clock);
        using var secondClient = Client(factory, seed.Guest, seed.Clock);
        var responses = await Task.WhenAll(Send(firstClient, seed.Board.Id, "First tab", ct),
            Send(secondClient, seed.Board.Id, "Second tab", ct));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        Assert.Equal(1, await db.ChatOutboxEvents.CountAsync(ct));
    }

    [Fact]
    public async Task Normal_mode_and_new_settings_revision_do_not_reuse_stale_cooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct, slowModeSeconds: 30);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        using var first = await Send(guest, seed.Board.Id, "Slow", ct);
        await using (var db = postgres.CreateContext())
            await db.BoardChatSettings.ExecuteUpdateAsync(set => set.SetProperty(item => item.SlowModeSeconds, 0)
                .SetProperty(item => item.SettingsRevision, 2), ct);
        using var normal = await Send(guest, seed.Board.Id, "Normal", ct);
        Assert.Equal(HttpStatusCode.Created, normal.StatusCode);
        Assert.Null((await Result(normal, ct)).NextSendAllowedAt);
        await using (var db = postgres.CreateContext())
            await db.BoardChatSettings.ExecuteUpdateAsync(set => set.SetProperty(item => item.SlowModeSeconds, 60)
                .SetProperty(item => item.SettingsRevision, 3), ct);
        using var newPolicy = await Send(guest, seed.Board.Id, "New policy", ct);
        Assert.Equal(HttpStatusCode.Created, newPolicy.StatusCode);
        Assert.Equal(seed.Clock.GetUtcNow().AddSeconds(60), (await Result(newPolicy, ct)).NextSendAllowedAt);
    }

    [Fact]
    public async Task Latest_and_older_pages_cover_equal_timestamp_messages_without_duplicates_and_survive_reload()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        string cursor;
        await using (var factory = new ChatFactory(postgres, seed.Clock))
        {
            using var client = Client(factory, seed.Owner, seed.Clock);
            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => Send(client, seed.Board.Id, $"Message {index}", ct)));
            foreach (var response in responses) { Assert.Equal(HttpStatusCode.Created, response.StatusCode); response.Dispose(); }
            var page = await Page(client, seed.Board.Id, "?limit=2", ct);
            Assert.Equal(new[] { "5", "6" }, page.Items.Select(item => item.Sequence));
            Assert.True(page.HasMore);
            cursor = page.OlderCursor!;
        }
        await using var reloaded = new ChatFactory(postgres, seed.Clock);
        using var reader = Client(reloaded, seed.Guest, seed.Clock);
        var middle = await Page(reader, seed.Board.Id, $"?limit=2&before={cursor}", ct);
        var oldest = await Page(reader, seed.Board.Id, $"?limit=2&before={middle.OlderCursor}", ct);
        Assert.Equal(new[] { "3", "4" }, middle.Items.Select(item => item.Sequence));
        Assert.Equal(new[] { "1", "2" }, oldest.Items.Select(item => item.Sequence));
        Assert.False(oldest.HasMore);
    }

    [Fact]
    public async Task Catch_up_uses_fixed_boundary_and_recovers_new_arrivals_on_next_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        var empty = await Page(owner, seed.Board.Id, "", ct);
        for (var index = 0; index < 3; index++) { using var response = await Send(owner, seed.Board.Id, $"Message {index}", ct); }
        var first = await Page(owner, seed.Board.Id, $"?limit=2&after={empty.NewerCursor}", ct);
        Assert.Equal(new[] { "1", "2" }, first.Items.Select(item => item.Sequence));
        Assert.True(first.HasMore);
        using var incoming = await Send(owner, seed.Board.Id, "Arrived during catch-up", ct);
        var second = await Page(owner, seed.Board.Id, $"?limit=2&after={first.NewerCursor}&through={first.CatchUpThrough}", ct);
        Assert.Equal("3", Assert.Single(second.Items).Sequence);
        Assert.False(second.HasMore);
        var nextPass = await Page(owner, seed.Board.Id, $"?after={second.NewerCursor}", ct);
        Assert.Equal("4", Assert.Single(nextPass.Items).Sequence);
    }

    [Fact]
    public async Task Cursors_are_board_bound_tamper_resistant_and_page_arguments_are_bounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var created = await Send(owner, seed.Board.Id, "Cursor", ct);
        var cursor = (await Result(created, ct)).Message.Cursor;
        var other = new Board { Title = "Another owned board" };
        other.Memberships.Add(new BoardMembership { UserId = seed.Owner.Id, Role = BoardRole.Owner, CanEdit = true });
        await using (var db = postgres.CreateContext()) { db.Add(other); await db.SaveChangesAsync(ct); }
        using var crossBoard = await owner.GetAsync($"{Path(other.Id)}?after={cursor}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, crossBoard.StatusCode);
        foreach (var query in new[] { "?limit=0", "?limit=101", "?after=invalid", $"?after={cursor}&before={cursor}", $"?through={cursor}" })
        {
            using var rejected = await owner.GetAsync(Path(seed.Board.Id) + query, ct);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        var middle = cursor.Length / 2;
        var tampered = cursor[..middle] + (cursor[middle] == 'a' ? 'b' : 'a') + cursor[(middle + 1)..];
        using var tamper = await owner.GetAsync($"{Path(seed.Board.Id)}?after={tampered}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, tamper.StatusCode);
    }

    [Fact]
    public async Task Invalid_messages_and_oversized_known_or_unknown_length_json_create_no_records()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        foreach (var request in new[] { new SendChatMessageRequest(Guid.Empty, "Text"),
            new SendChatMessageRequest(Guid.NewGuid(), " \t\n"), new SendChatMessageRequest(Guid.NewGuid(), null),
            new SendChatMessageRequest(Guid.NewGuid(), new string('a', 4001)) })
        {
            using var response = await owner.PostAsJsonAsync(Path(seed.Board.Id), request, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var payload = "{\"clientMessageId\":\"" + Guid.NewGuid() + "\",\"body\":\"" + new string('a', 40000) + "\"}";
        using var known = await owner.PostAsync(Path(seed.Board.Id), new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, known.StatusCode);
        using var stream = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(payload)));
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var unknown = await owner.PostAsync(Path(seed.Board.Id), stream, ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, unknown.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.False(await db.ChatMessages.AnyAsync(ct));
        Assert.False(await db.ChatOutboxEvents.AnyAsync(ct));
        Assert.Equal(0, (await db.BoardChatSettings.SingleAsync(ct)).LastMessageSequence);
    }

    [Fact]
    public async Task Failure_after_sql_writes_rolls_back_message_outbox_sequence_and_cooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct, slowModeSeconds: 30);
        var operation = Guid.NewGuid();
        await using (var failing = new ChatFactory(postgres, seed.Clock, interceptor: new FailAfterWrite()))
        {
            using var guest = Client(failing, seed.Guest, seed.Clock);
            using var response = await Send(guest, seed.Board.Id, "Rollback", ct, operation);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        await using (var db = postgres.CreateContext())
        {
            Assert.False(await db.ChatMessages.AnyAsync(ct));
            Assert.False(await db.ChatOutboxEvents.AnyAsync(ct));
            Assert.Equal(0, (await db.BoardChatSettings.SingleAsync(ct)).LastMessageSequence);
            Assert.False(await db.BoardMemberChatStates.AnyAsync(ct));
        }
        await using var recovered = new ChatFactory(postgres, seed.Clock);
        using var client = Client(recovered, seed.Guest, seed.Clock);
        using var retried = await Send(client, seed.Board.Id, "Rollback", ct, operation);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }

    [Fact]
    public async Task Send_rate_limit_is_separate_from_normal_mode_and_partitioned_by_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock, sendLimit: 2);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        using var first = await Send(owner, seed.Board.Id, "One", ct);
        using var second = await Send(owner, seed.Board.Id, "Two", ct);
        using var denied = await Send(owner, seed.Board.Id, "Three", ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal("chat_rate_limited", await Error(denied, ct));
        Assert.NotNull(denied.Headers.RetryAfter);
        using var independent = await Send(guest, seed.Board.Id, "Guest quota", ct);
        Assert.Equal(HttpStatusCode.Created, independent.StatusCode);
    }

    [Fact]
    public async Task Removal_waits_for_authorized_send_then_revokes_future_access()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        var gate = new SendRemovalGate();
        await using var factory = new ChatFactory(postgres, seed.Clock, interceptor: gate);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        var send = Send(guest, seed.Board.Id, "In flight before removal", ct);
        Task<HttpResponseMessage>? removal = null;
        var removedBeforeRelease = false;
        try
        {
            await gate.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            removal = owner.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", ct);
            await gate.RemovalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await Task.Delay(100, ct);
            removedBeforeRelease = removal.IsCompleted;
        }
        finally { gate.Release.TrySetResult(); }
        using var sent = await send;
        using var removed = await removal!;
        Assert.False(removedBeforeRelease);
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var denied = await Send(guest, seed.Board.Id, "After removal", ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        Assert.False(await db.BoardMemberChatStates.AnyAsync(state => state.UserId == seed.Guest.Id, ct));
    }

    [Fact]
    public async Task Newly_created_board_initializes_chat_once_and_preserves_timestamp_on_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var createdBoard = await owner.PostAsJsonAsync("/api/boards", new CreateBoardRequest("New chat board"), ct);
        Assert.Equal(HttpStatusCode.Created, createdBoard.StatusCode);
        var board = Assert.IsType<BoardDetailDto>(await createdBoard.Content.ReadFromJsonAsync<BoardDetailDto>(ct));
        Assert.Empty((await Page(owner, board.Id, "", ct)).Items);
        seed.Clock.Advance(TimeSpan.FromTicks(7));
        var operation = Guid.NewGuid();
        var responses = await Task.WhenAll(Send(owner, board.Id, "First", ct, operation), Send(owner, board.Id, "Second", ct));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var first = (await Result(responses[0], ct)).Message;
            using var replay = await Send(owner, board.Id, "First", ct, operation);
            Assert.Equal(first.CreatedAt, (await Result(replay, ct)).Message.CreatedAt);
            using var reloaded = await owner.GetAsync($"{Path(board.Id)}/{first.Id}", ct);
            Assert.Equal(first.CreatedAt, (await reloaded.Content.ReadFromJsonAsync<ChatMessageDto>(ct))!.CreatedAt);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        await using var db = postgres.CreateContext();
        Assert.Equal(2, (await db.BoardChatSettings.SingleAsync(settings => settings.BoardId == board.Id, ct)).LastMessageSequence);
        Assert.Single(await db.BoardMemberChatStates.Where(state => state.BoardId == board.Id).ToListAsync(ct));
        Assert.Equal(new[] { "1", "2" }, (await Page(owner, board.Id, "", ct)).Items.Select(item => item.Sequence));
    }

    [Fact]
    public async Task Read_only_guest_creates_structured_scheduled_post_visible_in_history_and_outbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        var request = Scheduled(Guid.NewGuid(),
            new DateTime(2026, 10, 9, 14, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 10, 9, 15, 30, 0, DateTimeKind.Unspecified),
            "Asia/Beirut", " Deploy beta build ", " Release notes ");
        using var response = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await Result(response, ct);
        Assert.Equal("scheduledTask", sent.Message.Type);
        Assert.Null(sent.Message.Body);
        Assert.Equal(seed.Guest.Id, sent.Message.Sender.UserId);
        var scheduled = Assert.IsType<ScheduledChatTaskDto>(sent.Message.ScheduledTask);
        Assert.Equal("Deploy beta build", scheduled.Title);
        Assert.Equal("Release notes", scheduled.Description);
        Assert.Equal("Asia/Beirut", scheduled.TimeZoneId);
        Assert.Equal(180, scheduled.OriginalOffsetMinutes);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero), scheduled.StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.Zero), scheduled.EndsAtUtc);
        Assert.Equal(sent.Message.Id, Assert.Single((await Page(guest, seed.Board.Id, "", ct)).Items).Id);
        using var single = await guest.GetAsync($"{Path(seed.Board.Id)}/{sent.Message.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.NotNull((await single.Content.ReadFromJsonAsync<ChatMessageDto>(ct))?.ScheduledTask);
        await using var db = postgres.CreateContext();
        Assert.Single(await db.ScheduledChatTasks.ToListAsync(ct));
        Assert.Single(await db.ChatOutboxEvents.Where(item => item.Kind == ChatOutboxEventKind.MessageCreated).ToListAsync(ct));
        Assert.False((await db.BoardMemberships.SingleAsync(member => member.UserId == seed.Guest.Id, ct)).CanEdit);
        Assert.Equal(seed.Board.UpdatedAt, (await db.Boards.SingleAsync(ct)).UpdatedAt);
    }

    [Fact]
    public async Task Calendar_export_is_stable_and_requires_current_board_membership()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        using var outsider = Client(factory, User("outsider"), seed.Clock);
        using var anonymous = factory.CreateClient();
        using var created = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), Scheduled(Guid.NewGuid(),
            new DateTime(2026, 10, 9, 14, 0, 0), new DateTime(2026, 10, 9, 15, 30, 0),
            "Asia/Beirut", "Deploy beta", "Review notes"), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var messageId = (await Result(created, ct)).Message.Id;
        var exportPath = $"{Path(seed.Board.Id)}/{messageId}/calendar.ics";
        using var download = await guest.GetAsync(exportPath, ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/calendar", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", download.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains($"wukna-task-{messageId:N}.ics", download.Content.Headers.ContentDisposition?.ToString());
        var content = await download.Content.ReadAsStringAsync(ct);
        Assert.Contains($"UID:wukna-chat-{messageId:N}@wukna.invalid", content);
        Assert.Contains("DTSTART:20261009T110000Z", content);
        Assert.Contains("DTEND:20261009T123000Z", content);
        using var replay = await owner.GetAsync(exportPath, ct);
        Assert.Equal(content, await replay.Content.ReadAsStringAsync(ct));

        using var text = await Send(owner, seed.Board.Id, "Not a task", ct);
        var textId = (await Result(text, ct)).Message.Id;
        using var wrongType = await owner.GetAsync($"{Path(seed.Board.Id)}/{textId}/calendar.ics", ct);
        using var unrelated = await outsider.GetAsync(exportPath, ct);
        using var unauthenticated = await anonymous.GetAsync(exportPath, ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var otherCreated = await owner.PostAsJsonAsync("/api/boards", new CreateBoardRequest("Other board"), ct);
        Assert.Equal(HttpStatusCode.Created, otherCreated.StatusCode);
        var otherBoard = Assert.IsType<BoardDetailDto>(await otherCreated.Content.ReadFromJsonAsync<BoardDetailDto>(ct));
        using var wrongBoard = await owner.GetAsync($"{Path(otherBoard.Id)}/{messageId}/calendar.ics", ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongBoard.StatusCode);
        using var removal = await owner.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        using var removed = await guest.GetAsync(exportPath, ct);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
    }

    [Fact]
    public async Task Scheduled_post_replays_identically_and_shares_chat_cooldown_and_mute()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct, slowModeSeconds: 30);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest, seed.Clock);
        var operation = Guid.NewGuid();
        var request = Scheduled(operation, new DateTime(2026, 10, 9, 14, 0, 0));
        using var first = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), request, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var sent = await Result(first, ct);
        using var replay = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), request, ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await Result(replay, ct);
        Assert.True(replayed.IsReplay);
        Assert.Equal(sent.Message.Id, replayed.Message.Id);
        using var changed = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), request with { Title = "Changed" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("chat_operation_conflict", await Error(changed, ct));
        using var crossType = await Send(guest, seed.Board.Id, "Same operation", ct, operation);
        Assert.Equal(HttpStatusCode.Conflict, crossType.StatusCode);
        using var cooldown = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), request with { ClientMessageId = Guid.NewGuid() }, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, cooldown.StatusCode);
        Assert.Equal("chat_cooldown", await Error(cooldown, ct));
        seed.Clock.Advance(TimeSpan.FromSeconds(30));
        await using (var db = postgres.CreateContext())
            await db.BoardMemberChatStates.Where(state => state.UserId == seed.Guest.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(state => state.IsMuted, true), ct);
        using var muted = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), request with { ClientMessageId = Guid.NewGuid() }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, muted.StatusCode);
        Assert.Equal("chat_muted", await Error(muted, ct));
        using var mutedReplay = await guest.PostAsJsonAsync(TaskPath(seed.Board.Id), request, ct);
        Assert.Equal(HttpStatusCode.OK, mutedReplay.StatusCode);
        await using var verify = postgres.CreateContext();
        Assert.Equal(1, await verify.ChatMessages.CountAsync(ct));
        Assert.Equal(1, await verify.ScheduledChatTasks.CountAsync(ct));
        Assert.Equal(1, await verify.ChatOutboxEvents.CountAsync(ct));
    }

    [Fact]
    public async Task Scheduled_post_validates_zone_gap_overlap_offsets_range_and_membership()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(ct);
        await using var factory = new ChatFactory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner, seed.Clock);
        using var outsider = Client(factory, User("outsider"), seed.Clock);
        var path = TaskPath(seed.Board.Id);
        var overlap = Scheduled(Guid.NewGuid(), new DateTime(2026, 11, 1, 1, 30, 0),
            new DateTime(2026, 11, 1, 2, 30, 0), "America/New_York");
        var cases = new (CreateScheduledChatTaskRequest Request, string Error)[] {
            (Scheduled(Guid.NewGuid(), new DateTime(2026, 3, 8, 2, 30, 0), zone: "America/New_York"), "chat_invalid_local_time"),
            (overlap, "chat_ambiguous_local_time"),
            (overlap with { StartOffsetMinutes = -360 }, "chat_invalid_offset"),
            (overlap with { StartOffsetMinutes = -240, LocalEnd = new DateTime(2026, 11, 1, 1, 0, 0), EndOffsetMinutes = -240 }, "chat_invalid_task_range"),
            (Scheduled(Guid.NewGuid(), new DateTime(2026, 10, 9, 14, 0, 0), zone: "Invalid/Zone"), "chat_invalid_time_zone"),
            (Scheduled(Guid.NewGuid(), DateTime.UtcNow), "chat_invalid_scheduled_task"),
            (Scheduled(Guid.Empty, new DateTime(2026, 10, 9, 14, 0, 0)), "chat_invalid_scheduled_task")
        };
        foreach (var (request, expected) in cases)
        {
            using var response = await owner.PostAsJsonAsync(path, request, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(expected, await Error(response, ct));
        }
        using var denied = await outsider.PostAsJsonAsync(path, overlap with { StartOffsetMinutes = -240 }, ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var valid = await owner.PostAsJsonAsync(path, overlap with { StartOffsetMinutes = -240 }, ct);
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        var task = Assert.IsType<ScheduledChatTaskDto>((await Result(valid, ct)).Message.ScheduledTask);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), task.StartsAtUtc);
        Assert.Equal(-240, task.OriginalOffsetMinutes);
        using var second = await owner.PostAsJsonAsync(path, overlap with { ClientMessageId = Guid.NewGuid(), StartOffsetMinutes = -300 }, ct);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero),
            Assert.IsType<ScheduledChatTaskDto>((await Result(second, ct)).Message.ScheduledTask).StartsAtUtc);
        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.ScheduledChatTasks.CountAsync(ct));
    }

    private static CreateScheduledChatTaskRequest Scheduled(Guid operation, DateTime start, DateTime? end = null,
        string zone = "UTC", string title = "Scheduled", string? description = null) =>
        new(operation, title, description, start, end, zone, null, null);
    private static string TaskPath(Guid boardId) => $"/api/boards/{boardId}/chat/scheduled-tasks";

    private async Task<SeedData> Seed(CancellationToken ct, int slowModeSeconds = 0)
    {
        await postgres.ResetAsync(ct);
        var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var owner = User("owner");
        var guest = User("guest");
        guest.DisplayName = "Guest display";
        var other = User("other");
        var board = new Board { Title = "HTTP chat", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        board.Memberships.Add(new BoardMembership { User = owner, Role = BoardRole.Owner, CanEdit = true });
        board.Memberships.Add(new BoardMembership { User = guest, Role = BoardRole.Guest, CanEdit = false });
        board.Memberships.Add(new BoardMembership { User = other, Role = BoardRole.Guest, CanEdit = true });
        await using var db = postgres.CreateContext();
        db.Add(board);
        db.Add(new BoardChatSettings { BoardId = board.Id, SlowModeSeconds = slowModeSeconds });
        await db.SaveChangesAsync(ct);
        return new SeedData(board, owner, guest, other, clock);
    }

    private static User User(string prefix) => new() { Email = prefix + "@chat.test", NormalizedEmail = prefix.ToUpperInvariant() + "@CHAT.TEST" };
    private static string Path(Guid boardId) => $"/api/boards/{boardId}/chat/messages";
    private static HttpClient Client(WuknaWebApplicationFactory factory, User user, ManualTimeProvider clock)
    {
        var client = factory.CreateClient();
        var token = new JwtTokenGenerator(new JwtOptions
        {
            Issuer = WuknaWebApplicationFactory.JwtIssuer, Audience = WuknaWebApplicationFactory.JwtAudience,
            SigningKey = WuknaWebApplicationFactory.JwtSigningKey, AccessTokenMinutes = 60
        }, clock).CreateAccessToken(user).Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, Guid boardId, string body, CancellationToken ct, Guid? operation = null) =>
        client.PostAsJsonAsync(Path(boardId), new SendChatMessageRequest(operation ?? Guid.NewGuid(), body), ct);
    private static async Task<ChatSendResultDto> Result(HttpResponseMessage response, CancellationToken ct) =>
        Assert.IsType<ChatSendResultDto>(await response.Content.ReadFromJsonAsync<ChatSendResultDto>(ct));
    private static async Task<ChatHistoryPageDto> Page(HttpClient client, Guid boardId, string query, CancellationToken ct)
    {
        using var response = await client.GetAsync(Path(boardId) + query, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<ChatHistoryPageDto>(await response.Content.ReadFromJsonAsync<ChatHistoryPageDto>(ct));
    }
    private static async Task<string?> Error(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(ct))?["error"].GetString();
    private sealed record SeedData(Board Board, User Owner, User Guest, User OtherGuest, ManualTimeProvider Clock);

    private sealed class ChatFactory : WuknaWebApplicationFactory
    {
        private readonly PostgresFixture postgres;
        private readonly int? sendLimit;
        private readonly IInterceptor? interceptor;

        public ChatFactory(PostgresFixture postgres, ManualTimeProvider clock,
            int? sendLimit = null, IInterceptor? interceptor = null) : base(postgres, clock)
        {
            this.postgres = postgres;
            this.sendLimit = sendLimit;
            this.interceptor = interceptor;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                if (sendLimit is { } limit) services.PostConfigure<ChatOptions>(options => options.MessageSendsPerMinute = limit);
                if (interceptor is null) return;
                services.RemoveAll<WuknaDbContext>();
                services.RemoveAll<DbContextOptions<WuknaDbContext>>();
                services.AddDbContext<WuknaDbContext>(options => options.UseNpgsql(postgres.ConnectionString)
                    .UseSnakeCaseNamingConvention().AddInterceptors(interceptor));
            });
        }
    }
    private sealed class FailAfterWrite : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ChatMessage>().Any())
                throw new InvalidOperationException("Simulated failure after database writes.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class SendRemovalGate : DbCommandInterceptor
    {
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RemovalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText.Replace("\"", "", StringComparison.Ordinal);
            if (sql.Contains("INSERT INTO chat_messages", StringComparison.Ordinal))
            {
                SendEntered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (sql.Contains("FROM board_memberships", StringComparison.Ordinal) &&
                sql.Contains("FOR UPDATE", StringComparison.Ordinal)) RemovalEntered.TrySetResult();
            return result;
        }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
