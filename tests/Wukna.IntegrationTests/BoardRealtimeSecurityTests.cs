namespace Wukna.IntegrationTests;

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wukna.Features.Board;
using Wukna.Features.Notes;
using Wukna.Features.Realtime;
using Xunit;

public sealed class BoardRealtimeSecurityTests(PostgresFixture postgres)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pending_post_commit_transport_send_does_not_block_new_membership_coordination()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var initiated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new BoardTransportSpy
        {
            Publication = eventName =>
            {
                if (eventName != BoardRealtimeEvents.BoardAccessRevoked) return Task.CompletedTask;
                initiated.TrySetResult();
                return release.Task;
            }
        };
        // HTTP scheduling test with a component transport; this does not inject a
        // native group-removal failure into real sockets.
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock)
        {
            ConfigureOverrides = services => services.AddSingleton<IHubContext<BoardHub>>(transport)
        };
        using var owner = factory.Client(seed.Owner);
        var registry = factory.Services.GetRequiredService<IBoardConnectionRegistry>();
        Assert.True(registry.Add(seed.Board.Id, seed.Guest.Id, "pending-control-tab").Changed);
        var removal = owner.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", Ct);
        try
        {
            await initiated.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await using (await registry.EnterLifecycleAsync(seed.Board.Id, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct))
            {
                Assert.Empty(registry.GetConnections(seed.Board.Id, seed.Guest.Id));
                Assert.False(removal.IsCompleted);
            }
            Assert.Contains(transport.Deliveries, delivery => delivery.EventName == BoardRealtimeEvents.BoardSummaryRemoved);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.NoContent, (await removal).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_restores_access_only_after_verified_rollback_or_settled_reconciliation(bool failAcknowledgement)
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var gate = new RemovalFailureGate(failAcknowledgement);
        var transport = new BoardTransportSpy();
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock, gate, gate.Observer)
        {
            ConfigureOverrides = services => services.AddSingleton<IHubContext<BoardHub>>(transport)
        };
        using var owner = factory.Client(seed.Owner);
        var registry = factory.Services.GetRequiredService<IBoardConnectionRegistry>();
        registry.Add(seed.Board.Id, seed.Guest.Id, "guest"); registry.Add(seed.Board.Id, seed.Peer.Id, "peer");
        var publisher = factory.Services.GetRequiredService<IBoardRealtimePublisher>();
        var removal = owner.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", Ct);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await publisher.PublishBoardAsync(seed.Board.Id, BoardRealtimeEvents.BoardUpdated,
                new BoardUpdatedEvent(seed.Board.Id, "during-outcome", seed.Clock.GetUtcNow()), Ct);
            Assert.Equal(["peer"], Assert.Single(transport.Deliveries).Targets);
            await using var db = postgres.CreateContext();
            Assert.Equal(!failAcknowledgement, await db.BoardMemberships.AnyAsync(member =>
                member.BoardId == seed.Board.Id && member.UserId == seed.Guest.Id, Ct));
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.InternalServerError, (await removal).StatusCode);
        if (failAcknowledgement)
        {
            Assert.Empty(registry.GetConnections(seed.Board.Id, seed.Guest.Id));
            Assert.Single(registry.PendingAccessChanges(8));
            await factory.Services.GetRequiredService<BoardGroupCleanupService>().ProcessBatchAsync(Ct);
        }
        Assert.Empty(registry.PendingAccessChanges(8));
        Assert.Equal(!failAcknowledgement, registry.Contains(seed.Board.Id, seed.Guest.Id, "guest"));
        await publisher.PublishBoardAsync(seed.Board.Id, BoardRealtimeEvents.BoardUpdated,
            new BoardUpdatedEvent(seed.Board.Id, "after-outcome", seed.Clock.GetUtcNow()), Ct);
        Assert.Equal(!failAcknowledgement, transport.Deliveries.Last().Targets.Contains("guest"));
    }

    [Fact]
    public async Task Removal_excludes_two_live_tabs_and_old_JWT_reconnects_until_a_fresh_authorized_join_after_reinvitation()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock);
        using var owner = factory.Client(seed.Owner);
        await using var first = factory.Connection(owner, seed.Guest);
        await using var second = factory.Connection(owner, seed.Guest);
        await using var peer = factory.Connection(owner, seed.Peer);
        var firstEvents = new BoardSocketObservation(first); var secondEvents = new BoardSocketObservation(second); var peerEvents = new BoardSocketObservation(peer);
        foreach (var socket in new[] { first, second, peer }) { await socket.StartAsync(Ct); await socket.InvokeAsync("JoinBoard", seed.Board.Id, Ct); }
        var registry = factory.Services.GetRequiredService<IBoardConnectionRegistry>();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", Ct)).StatusCode);
        await Rename(owner, seed.Board.Id, "private-after-removal");
        foreach (var observation in new[] { firstEvents, secondEvents, peerEvents }) await observation.FenceAsync(factory, Ct);
        Assert.Empty(firstEvents.Titles); Assert.Empty(secondEvents.Titles);
        Assert.Equal(["private-after-removal"], peerEvents.Titles);
        Assert.Equal([seed.Board.Id], firstEvents.Revocations); Assert.Equal([seed.Board.Id], secondEvents.Revocations);
        Assert.Equal([seed.Board.Id], firstEvents.SummaryRemovals); Assert.Equal([seed.Board.Id], secondEvents.SummaryRemovals);
        Assert.Equal(HubConnectionState.Connected, first.State);
        Assert.Empty(registry.GetConnections(seed.Board.Id, seed.Guest.Id));
        Assert.Equal(2, registry.CleanupCandidates(DateTimeOffset.UtcNow, 32).Count);
        Assert.Contains("Forbidden", (await Assert.ThrowsAsync<HubException>(() => first.InvokeAsync("JoinBoard", seed.Board.Id, Ct))).Message);
        await using var reconnect = factory.Connection(owner, seed.Guest); // Same still-valid JWT.
        var reconnectEvents = new BoardSocketObservation(reconnect);
        await reconnect.StartAsync(Ct);
        Assert.Contains("Forbidden", (await Assert.ThrowsAsync<HubException>(() => reconnect.InvokeAsync("JoinBoard", seed.Board.Id, Ct))).Message);
        Assert.Equal(HttpStatusCode.NoContent, (await ChatControlTestSupport.Invite(owner, seed, seed.Guest, Ct)).StatusCode);
        await Rename(owner, seed.Board.Id, "private-before-fresh-join");
        foreach (var observation in new[] { firstEvents, secondEvents, reconnectEvents }) await observation.FenceAsync(factory, Ct);
        Assert.Empty(firstEvents.Titles); Assert.Empty(secondEvents.Titles); Assert.Empty(reconnectEvents.Titles);
        // An explicit authorized join creates a new generation, even on the same
        // live connection. Cleanup for its former generation must become obsolete.
        await first.InvokeAsync("JoinBoard", seed.Board.Id, Ct);
        await factory.Services.GetRequiredService<BoardGroupCleanupService>().ProcessBatchAsync(Ct);
        await Rename(owner, seed.Board.Id, "visible-after-fresh-join");
        foreach (var observation in new[] { firstEvents, secondEvents, reconnectEvents }) await observation.FenceAsync(factory, Ct);
        Assert.Equal(["visible-after-fresh-join"], firstEvents.Titles);
        Assert.Empty(secondEvents.Titles); Assert.Empty(reconnectEvents.Titles);
        Assert.True(registry.Contains(seed.Board.Id, seed.Guest.Id, first.ConnectionId!));
    }

    [Fact]
    public async Task Actual_commit_is_fenced_while_post_commit_processing_is_paused_and_a_join_races()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var gate = new RemovalCommitGate();
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock, gate, gate.Observer);
        using var owner = factory.Client(seed.Owner);
        await using var guest = factory.Connection(owner, seed.Guest); await using var peer = factory.Connection(owner, seed.Peer);
        var guestEvents = new BoardSocketObservation(guest); var peerEvents = new BoardSocketObservation(peer);
        foreach (var socket in new[] { guest, peer }) { await socket.StartAsync(Ct); await socket.InvokeAsync("JoinBoard", seed.Board.Id, Ct); }
        gate.Armed = true;
        var removal = owner.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{seed.Guest.Id}", Ct);
        try
        {
            await gate.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await using (var db = postgres.CreateContext()) Assert.False(await db.BoardMemberships.AnyAsync(member => member.BoardId == seed.Board.Id && member.UserId == seed.Guest.Id, Ct));
            var registry = factory.Services.GetRequiredService<IBoardConnectionRegistry>();
            Assert.Empty(registry.GetConnections(seed.Board.Id, seed.Guest.Id));
            var rejoin = guest.InvokeAsync("JoinBoard", seed.Board.Id, Ct);
            await factory.Services.GetRequiredService<IBoardRealtimePublisher>().PublishBoardAsync(seed.Board.Id,
                BoardRealtimeEvents.BoardUpdated, new BoardUpdatedEvent(seed.Board.Id, "after-effective-commit", seed.Clock.GetUtcNow()), Ct);
            await guestEvents.FenceAsync(factory, Ct); await peerEvents.FenceAsync(factory, Ct);
            Assert.Empty(guestEvents.Titles); Assert.Equal(["after-effective-commit"], peerEvents.Titles);
            Assert.False(rejoin.IsCompleted);
            gate.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.NoContent, (await removal).StatusCode);
            Assert.Contains("Forbidden", (await Assert.ThrowsAsync<HubException>(() => rejoin)).Message);
        }
        finally { gate.Release.TrySetResult(); await removal; }
    }

    [Fact]
    public async Task Board_deletion_delivers_existing_controls_and_excludes_all_subscriptions()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock);
        using var owner = factory.Client(seed.Owner);
        await using var guest = factory.Connection(owner, seed.Guest); var events = new BoardSocketObservation(guest);
        await guest.StartAsync(Ct); await guest.InvokeAsync("JoinBoard", seed.Board.Id, Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{seed.Board.Id}", Ct)).StatusCode);
        await factory.Services.GetRequiredService<IBoardRealtimePublisher>().PublishBoardAsync(seed.Board.Id,
            BoardRealtimeEvents.BoardUpdated, new BoardUpdatedEvent(seed.Board.Id, "must-not-arrive", seed.Clock.GetUtcNow()), Ct);
        await events.FenceAsync(factory, Ct);
        Assert.Empty(events.Titles); Assert.Equal([seed.Board.Id], events.Revocations); Assert.Equal([seed.Board.Id], events.SummaryRemovals);
        Assert.Empty(factory.Services.GetRequiredService<IBoardConnectionRegistry>().GetConnections(seed.Board.Id));
        Assert.Contains("Forbidden", (await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("JoinBoard", seed.Board.Id, Ct))).Message);
    }

    [Fact]
    public async Task Permission_downgrade_retains_viewing_and_frequent_cursor_publications_add_no_SQL()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var note = new Note { BoardId = seed.Board.Id, Kind = NoteKind.Standalone, Title = "Private note", PositionX = 1, PositionY = 1 };
        await using (var db = postgres.CreateContext())
        {
            (await db.BoardMemberships.FindAsync([seed.Board.Id, seed.Guest.Id], Ct))!.CanEdit = true;
            db.Notes.Add(note); await db.SaveChangesAsync(Ct);
        }
        var sql = new BoardSqlCounter();
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock, sql);
        using var owner = factory.Client(seed.Owner);
        await using var guest = factory.Connection(owner, seed.Guest); await using var peer = factory.Connection(owner, seed.Peer);
        var events = new BoardSocketObservation(guest);
        foreach (var socket in new[] { guest, peer }) { await socket.StartAsync(Ct); await socket.InvokeAsync("JoinBoard", seed.Board.Id, Ct); }
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PatchAsJsonAsync($"/api/boards/{seed.Board.Id}/members/{seed.Guest.Id}", new SetMemberPermissionRequest(false), Ct)).StatusCode);
        await Rename(owner, seed.Board.Id, "visible-to-viewer"); await events.FenceAsync(factory, Ct);
        Assert.Equal(["visible-to-viewer"], events.Titles); Assert.Empty(events.Revocations);
        Assert.Contains("Forbidden", (await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("StartNoteEditing", new StartNoteEditingRequest(seed.Board.Id, note.Id, 1), Ct))).Message);
        sql.Reset();
        for (var sequence = 1; sequence <= 20; sequence++)
            await guest.InvokeAsync("MoveBoardCursor", new MoveBoardCursorRequest(seed.Board.Id, sequence, sequence, sequence), Ct);
        await guest.InvokeAsync("StopBoardCursor", new StopBoardCursorRequest(seed.Board.Id, 21), Ct);
        Assert.Equal(0, sql.Count);
    }

    private async Task Rename(HttpClient owner, Guid board, string title) =>
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/boards/{board}", new RenameBoardRequest(title), Ct)).StatusCode);

    private sealed class RemovalCommitGate : DbTransactionInterceptor
    {
        public bool Armed;
        private DbContext? removalContext;
        public SaveChangesInterceptor Observer => new RemovalObserver(this);
        private int entered;
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (!Armed || !ReferenceEquals(data.Context, removalContext) ||
                Interlocked.CompareExchange(ref entered, 1, 0) != 0) return;
            Committed.TrySetResult(); await Release.Task.WaitAsync(ct);
        }
        private sealed class RemovalObserver(RemovalCommitGate gate) : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
                InterceptionResult<int> result, CancellationToken ct = default)
            {
                if (gate.Armed && data.Context?.ChangeTracker.Entries<BoardMembership>().Any(entry => entry.State == EntityState.Deleted) == true)
                    gate.removalContext = data.Context;
                return ValueTask.FromResult(result);
            }
        }
    }

    private sealed class RemovalFailureGate(bool failAcknowledgement) : DbTransactionInterceptor
    {
        private DbContext? removalContext;
        private bool FailAcknowledgement => failAcknowledgement;
        public SaveChangesInterceptor Observer => new RemovalObserver(this);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (!failAcknowledgement || !ReferenceEquals(data.Context, removalContext)) return;
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            throw new IOException("test-only commit acknowledgement failure");
        }
        private sealed class RemovalObserver(RemovalFailureGate gate) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
                InterceptionResult<int> result, CancellationToken ct = default)
            {
                if (data.Context?.ChangeTracker.Entries<BoardMembership>().Any(entry => entry.State == EntityState.Deleted) != true)
                    return result;
                gate.removalContext = data.Context;
                if (!gate.FailAcknowledgement)
                {
                    gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(ct);
                    throw new DbUpdateException("test-only membership persistence failure");
                }
                return result;
            }
        }
    }
}
