namespace Wukna.IntegrationTests;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Wukna.Features.Realtime;
using Xunit;

// Component fault injection only: these tests verify selection and bookkeeping,
// not actual socket non-delivery while native group removal fails.
public sealed class BoardGroupCleanupTests
{
    [Fact]
    public async Task Failed_cleanup_preserves_controls_and_excludes_all_revoked_tabs_from_sensitive_sends()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid(); var guest = Guid.NewGuid();
        registry.Add(board, guest, "tab-1"); registry.Add(board, guest, "tab-2"); registry.Add(board, Guid.NewGuid(), "peer");
        var transport = new BoardTransportSpy { Removal = _ => Task.FromException(new IOException("test-only cleanup failure")) };
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = transport.Worker(registry, clock, services.GetRequiredService<IServiceScopeFactory>());
        var publisher = transport.Publisher(registry);
        var change = registry.BeginAccessChange(board, [guest]); registry.CompleteAccessChange(change, true);
        await publisher.RevokeBoardAccessAsync(board, guest, cancellationToken: TestContext.Current.CancellationToken, change: change);
        await publisher.PublishUsersAsync([guest], BoardRealtimeEvents.BoardSummaryRemoved, new BoardSummaryRemovedEvent(board), TestContext.Current.CancellationToken);
        await worker.ProcessBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, transport.RemovalAttempts);
        await publisher.PublishBoardAsync(board, BoardRealtimeEvents.BoardUpdated, new BoardUpdatedEvent(board, "Private", clock.GetUtcNow()), TestContext.Current.CancellationToken);
        var sensitive = Assert.Single(transport.Deliveries, item => item.EventName == BoardRealtimeEvents.BoardUpdated);
        Assert.Equal(["peer"], sensitive.Targets);
        Assert.Equal(new[] { "tab-1", "tab-2" }, Assert.Single(transport.Deliveries, item => item.EventName == BoardRealtimeEvents.BoardAccessRevoked).Targets.Order());
        Assert.Single(transport.Deliveries, item => item.EventName == BoardRealtimeEvents.BoardSummaryRemoved);
        clock.Advance(TimeSpan.FromSeconds(1));
        transport.Removal = _ => Task.CompletedTask;
        await worker.ProcessBatchAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, transport.RemovalAttempts);
        Assert.Empty(registry.CleanupCandidates(clock.GetUtcNow().AddDays(1), 32));
        Assert.Empty(registry.GetConnections(board, guest));
    }

    [Fact]
    public async Task Retry_selected_before_a_fresh_join_cannot_remove_the_new_generation()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid(); var guest = Guid.NewGuid();
        registry.Add(board, guest, "tab"); registry.RemoveUser(board, guest);
        var record = Assert.Single(registry.CleanupCandidates(DateTimeOffset.UtcNow, 32));
        var transport = new BoardTransportSpy();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = transport.Worker(registry, TimeProvider.System, services.GetRequiredService<IServiceScopeFactory>());
        var lifecycle = await registry.EnterLifecycleAsync(board, TestContext.Current.CancellationToken);
        var retry = worker.ProcessCleanupAsync(record, TestContext.Current.CancellationToken);
        registry.Add(board, guest, "tab");
        await lifecycle.DisposeAsync();
        await retry;
        Assert.Equal(0, transport.RemovalAttempts);
        Assert.True(registry.Contains(board, guest, "tab"));
    }

    [Fact]
    public async Task Cleanup_ignoring_cancellation_keeps_actual_concurrency_bounded_and_blocks_late_removal_of_new_joins()
    {
        var registry = new BoardConnectionRegistry(); var guest = Guid.NewGuid();
        var boards = new Dictionary<string, Guid>();
        for (var index = 0; index < 8; index++)
        {
            var targetBoard = Guid.NewGuid();
            var connection = "tab-" + index; boards[connection] = targetBoard;
            registry.Add(targetBoard, guest, connection);
            registry.RemoveUser(targetBoard, guest);
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new ConcurrentQueue<string>();
        var transport = new BoardTransportSpy { Removal = id => { attempted.Enqueue(id); return release.Task; } };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = transport.Worker(registry, TimeProvider.System, services.GetRequiredService<IServiceScopeFactory>());
        string? blocked = null;
        try
        {
            await worker.ProcessBatchAsync(TestContext.Current.CancellationToken);
            Assert.InRange(transport.RemovalAttempts, 1, 4);
            await worker.ProcessBatchAsync(TestContext.Current.CancellationToken);
            Assert.Equal(4, transport.RemovalAttempts);
            foreach (var id in attempted)
                Assert.Throws<InvalidOperationException>(() => registry.Add(boards[id], guest, id));
            blocked = attempted.First();
        }
        finally { release.TrySetResult(); }
        // Completion is asynchronous; bounded polling only waits for native tasks
        // to settle before testing a new explicit registration.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            try { registry.Add(boards[blocked!], guest, blocked!); break; }
            catch (InvalidOperationException) { await Task.Delay(10, timeout.Token); }
        }
        Assert.True(registry.Contains(boards[blocked!], guest, blocked!));
    }
}
