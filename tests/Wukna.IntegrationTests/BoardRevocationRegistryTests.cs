namespace Wukna.IntegrationTests;

using Wukna.Features.Realtime;
using Xunit;

public sealed class BoardRevocationRegistryTests
{
    [Fact]
    public async Task Exclusion_precedes_commit_and_verified_rollback_restores_only_remaining_connections()
    {
        var registry = new BoardConnectionRegistry();
        var board = Guid.NewGuid(); var guest = Guid.NewGuid(); var peer = Guid.NewGuid();
        registry.Add(board, guest, "tab-1"); registry.Add(board, guest, "tab-2"); registry.Add(board, peer, "peer");
        var oldRevision = registry.PublicationRevision(board)!.Value;
        await using var lifecycle = await registry.EnterLifecycleAsync(board, TestContext.Current.CancellationToken);
        var change = registry.BeginAccessChange(board, [guest]);
        Assert.False(registry.Contains(board, guest, "tab-1"));
        Assert.Equal(["peer"], registry.GetConnections(board));
        Assert.Null(registry.PublicationRevision(board));
        Assert.Throws<BoardRecipientsChangedException>(() => { _ = registry.StartUserPublication(board, oldRevision, () => Task.CompletedTask); });
        registry.RemoveConnection("tab-2");
        registry.CompleteAccessChange(change, committed: false);
        Assert.True(registry.Contains(board, guest, "tab-1"));
        Assert.False(registry.Contains(board, guest, "tab-2"));
        Assert.NotEqual(oldRevision, registry.PublicationRevision(board));
    }

    [Fact]
    public async Task An_asynchronous_send_does_not_hold_the_monitor_or_delay_revocation()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid(); var guest = Guid.NewGuid();
        registry.Add(board, guest, "guest"); registry.Add(board, Guid.NewGuid(), "peer");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = registry.StartPublication(board, null, recipients => { Assert.Contains("guest", recipients); return release.Task; });
        try
        {
            var change = await Task.Run(() => registry.BeginAccessChange(board, [guest]))
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            registry.CompleteAccessChange(change, committed: true);
            for (var index = 0; index < 50; index++)
                await registry.StartPublication(board, null, recipients => { Assert.Equal(["peer"], recipients); return Task.CompletedTask; });
            Assert.False(before.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await before;
    }

    [Fact]
    public async Task A_join_waits_for_the_lifecycle_gate_and_cannot_register_during_an_uncertain_outcome()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid(); var user = Guid.NewGuid();
        var lease = await registry.EnterLifecycleAsync(board, TestContext.Current.CancellationToken);
        var change = registry.BeginAccessChange(board, [user]);
        var waiting = registry.EnterLifecycleAsync(board, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        await lease.DisposeAsync();
        await using var join = await waiting.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => registry.Add(board, user, "new-tab"));
        Assert.Null(registry.PublicationRevision(board));
        registry.ReconcileAccessChange(change, boardExists: true, new HashSet<Guid> { user });
        Assert.True(registry.Add(board, user, "new-tab").Changed);
    }

    [Fact]
    public void Re_invitation_does_not_restore_old_tabs_and_old_cleanup_cannot_touch_a_fresh_join()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid(); var user = Guid.NewGuid();
        registry.Add(board, user, "old-tab"); registry.Add(board, user, "other-tab");
        var removed = registry.BeginAccessChange(board, [user]); registry.CompleteAccessChange(removed, true);
        var oldCleanup = registry.CleanupCandidates(DateTimeOffset.UtcNow, 32).Single(record => record.ConnectionId == "old-tab");
        var invitation = registry.BeginAccessChange(board); registry.CompleteAccessChange(invitation, true);
        Assert.Empty(registry.GetConnections(board, user));
        registry.Add(board, user, "old-tab"); // Models an explicit fresh, DB-authorized JoinBoard.
        Assert.Equal(["old-tab"], registry.GetConnections(board, user));
        Assert.False(registry.ClaimCleanup(oldCleanup));
        registry.FinishCleanup(oldCleanup, true, DateTimeOffset.UtcNow);
        Assert.True(registry.Contains(board, user, "old-tab"));
        Assert.False(registry.Contains(board, user, "other-tab"));
    }

    [Fact]
    public void An_unsettled_native_cleanup_blocks_reuse_of_that_connection_id()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid(); var user = Guid.NewGuid();
        registry.Add(board, user, "tab"); registry.RemoveUser(board, user);
        var record = Assert.Single(registry.CleanupCandidates(DateTimeOffset.UtcNow, 1));
        Assert.True(registry.ClaimCleanup(record));
        Assert.Throws<InvalidOperationException>(() => registry.Add(board, user, "tab"));
        registry.FinishCleanup(record, false, DateTimeOffset.UtcNow);
        registry.Add(board, user, "tab");
        Assert.False(registry.ClaimCleanup(record));
    }

    [Fact]
    public async Task Deletion_excludes_every_member_before_commit_and_stale_user_batches_after_commit()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid();
        registry.Add(board, Guid.NewGuid(), "owner"); registry.Add(board, Guid.NewGuid(), "guest");
        var revision = registry.PublicationRevision(board)!.Value;
        var deletion = registry.BeginAccessChange(board, deleteBoard: true);
        var called = false;
        await registry.StartPublication(board, null, _ => { called = true; return Task.CompletedTask; });
        registry.CompleteAccessChange(deletion, true);
        await registry.StartPublication(board, null, _ => { called = true; return Task.CompletedTask; });
        Assert.False(called);
        Assert.Throws<BoardRecipientsChangedException>(() => { _ = registry.StartUserPublication(board, revision, () => Task.CompletedTask); });
        Assert.Equal(2, registry.CleanupCandidates(DateTimeOffset.UtcNow, 32).Count);
    }

    [Fact]
    public void Cleanup_retry_exhaustion_is_bounded_and_disconnect_releases_retained_ids()
    {
        var registry = new BoardConnectionRegistry(); var board = Guid.NewGuid(); var user = Guid.NewGuid();
        registry.Add(board, user, "tab"); registry.RemoveUser(board, user);
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var record = Assert.Single(registry.CleanupCandidates(now, 32));
            Assert.True(registry.ClaimCleanup(record));
            registry.FinishCleanup(record, false, now);
            Assert.Empty(registry.CleanupCandidates(now, 32));
            now = now.AddMinutes(1);
        }
        Assert.Empty(registry.CleanupCandidates(now, 32));
        Assert.Empty(registry.GetConnections(board));
        registry.RemoveConnection("tab");
        registry.Add(board, user, "tab");
        Assert.True(registry.Contains(board, user, "tab"));
    }
}
