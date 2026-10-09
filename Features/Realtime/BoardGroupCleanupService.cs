namespace Wukna.Features.Realtime;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Board;
using Wukna.Shared.Data.AppDbContext;

// Process-local authorization requires a single serving API process. Retrying
// native cleanup never authorizes a recipient, and never publishes via groups.
public sealed class BoardGroupCleanupService(
    IBoardConnectionRegistry connections,
    IHubContext<BoardHub> hub,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<BoardGroupCleanupService> logger) : BackgroundService
{
    private readonly SemaphoreSlim operations = new(4, 4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ProcessBatchAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task ProcessBatchAsync(CancellationToken ct = default)
    {
        foreach (var change in connections.PendingAccessChanges(8, clock.GetUtcNow()))
            await ReconcileAsync(change, ct);
        await Task.WhenAll(connections.CleanupCandidates(clock.GetUtcNow(), 32)
            .Select(record => ProcessCleanupAsync(record, ct)));
    }

    public async Task ProcessCleanupAsync(BoardGroupCleanup record, CancellationToken ct = default)
    {
        if (!await operations.WaitAsync(0, ct)) return;
        var observing = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var lifecycle = await connections.EnterLifecycleAsync(record.BoardId, deadline.Token);
            if (!connections.ClaimCleanup(record)) return;
            Task removal;
            try { removal = hub.Groups.RemoveFromGroupAsync(record.ConnectionId, BoardRealtimeGroups.ForBoard(record.BoardId), deadline.Token); }
            catch (Exception exception) { removal = Task.FromException(exception); }
            observing = true;
            // The observer owns the permit until the ACTUAL native operation ends.
            // On timeout, the record stays in flight and a fresh join on this ID is
            // denied. Late completion cannot remove a renewed subscription. Hung
            // operations cannot grow beyond the four permits even if cancellation
            // is ignored by a lifetime manager.
            await ObserveCleanupAsync(record, removal).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Board group cleanup timed out or was cancelled for board {BoardId}, connection {ConnectionId}", record.BoardId, record.ConnectionId);
        }
        finally { if (!observing) operations.Release(); }
    }

    private async Task ObserveCleanupAsync(BoardGroupCleanup record, Task removal)
    {
        try
        {
            await removal;
            connections.FinishCleanup(record, succeeded: true, clock.GetUtcNow());
        }
        catch (Exception exception)
        {
            connections.FinishCleanup(record, succeeded: false, clock.GetUtcNow());
            logger.LogWarning(exception, "Board group cleanup failed; subscription remains excluded for board {BoardId}, connection {ConnectionId}", record.BoardId, record.ConnectionId);
        }
        finally { operations.Release(); }
    }

    private async Task ReconcileAsync(BoardAccessChange change, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var lifecycle = await connections.EnterLifecycleAsync(change.BoardId, deadline.Token);
            // A request may have settled the change while this worker waited.
            if (!connections.IsAccessChangePending(change)) return;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(deadline.Token);
            var exists = await BoardMembershipLocks.LockBoardAsync(db, change.BoardId, deadline.Token);
            var members = exists ? (await db.BoardMemberships.AsNoTracking().Where(member => member.BoardId == change.BoardId)
                .Select(member => member.UserId).ToArrayAsync(deadline.Token)).ToHashSet() : [];
            await transaction.CommitAsync(deadline.Token);
            connections.ReconcileAccessChange(change, exists, members);
            logger.LogInformation("Reconciled board membership transaction {ChangeId} for board {BoardId}", change.Id, change.BoardId);
        }
        catch (Exception exception)
        {
            connections.DeferReconciliation(change, clock.GetUtcNow());
            logger.LogWarning(exception, "Board membership reconciliation failed; access remains excluded for board {BoardId}", change.BoardId);
        }
    }
}
