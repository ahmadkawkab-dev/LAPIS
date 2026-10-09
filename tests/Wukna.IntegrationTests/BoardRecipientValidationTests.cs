namespace Wukna.IntegrationTests;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wukna.Features.Board;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

public sealed class BoardRecipientValidationTests(PostgresFixture postgres)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Failed_database_recipient_validation_suppresses_summaries_without_any_group_fallback()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var registry = new BoardConnectionRegistry(); var transport = new BoardTransportSpy();
        registry.Add(seed.Board.Id, seed.Guest.Id, "guest");
        await using var db = postgres.CreateContext(new FailReader());
        var dispatcher = new BoardRealtimeDispatcher(transport.Publisher(registry), registry, new BoardSummaryReader(db), db,
            NullLogger<BoardRealtimeDispatcher>.Instance);
        await dispatcher.BoardCreatedAsync(seed.Board.Id);
        Assert.Empty(transport.Deliveries);
    }

    [Fact]
    public async Task Recipients_read_before_revocation_are_rebuilt_instead_of_reused_after_commit()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var registry = new BoardConnectionRegistry(); var transport = new BoardTransportSpy(); var gate = new RecipientReadGate();
        await using var db = postgres.CreateContext(gate);
        var dispatcher = new BoardRealtimeDispatcher(transport.Publisher(registry), registry, new BoardSummaryReader(db), db,
            NullLogger<BoardRealtimeDispatcher>.Instance);
        var publication = dispatcher.BoardCreatedAsync(seed.Board.Id);
        try
        {
            await gate.Read.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await using var lifecycle = await registry.EnterLifecycleAsync(seed.Board.Id, Ct);
            var change = registry.BeginAccessChange(seed.Board.Id, [seed.Guest.Id]);
            await using var writer = postgres.CreateContext();
            await using var transaction = await writer.Database.BeginTransactionAsync(Ct);
            Assert.True(await BoardMembershipLocks.LockBoardAsync(writer, seed.Board.Id, Ct));
            writer.BoardMemberships.Remove((await writer.BoardMemberships.FindAsync([seed.Board.Id, seed.Guest.Id], Ct))!);
            await writer.SaveChangesAsync(Ct); await transaction.CommitAsync(Ct);
            registry.CompleteAccessChange(change, true);
        }
        finally { gate.Release.TrySetResult(); }
        await publication;
        Assert.DoesNotContain(transport.Deliveries, item => item.Targets.Contains(seed.Guest.Id.ToString("D")));
        Assert.Contains(transport.Deliveries, item => item.Targets.Contains(seed.Owner.Id.ToString("D")));
        Assert.Contains(transport.Deliveries, item => item.Targets.Contains(seed.Peer.Id.ToString("D")));
        Assert.True(gate.Reads >= 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uncertain_transaction_outcome_stays_excluded_until_settled_database_reconciliation(bool committed)
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var registry = new BoardConnectionRegistry(); var transport = new BoardTransportSpy();
        registry.Add(seed.Board.Id, seed.Guest.Id, "live-tab"); registry.Add(seed.Board.Id, seed.Guest.Id, "closed-tab");
        registry.Add(seed.Board.Id, seed.Peer.Id, "peer");
        await using (var lifecycle = await registry.EnterLifecycleAsync(seed.Board.Id, Ct))
        {
            await using var db = postgres.CreateContext(); await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            Assert.True(await BoardMembershipLocks.LockBoardAsync(db, seed.Board.Id, Ct));
            registry.BeginAccessChange(seed.Board.Id, [seed.Guest.Id]);
            db.BoardMemberships.Remove((await db.BoardMemberships.FindAsync([seed.Board.Id, seed.Guest.Id], Ct))!);
            await db.SaveChangesAsync(Ct);
            if (committed) await transaction.CommitAsync(Ct); else await transaction.RollbackAsync(Ct);
            // Simulates missing acknowledgement at the coordinator component boundary.
            Assert.Empty(registry.GetConnections(seed.Board.Id, seed.Guest.Id));
            registry.RemoveConnection("closed-tab");
        }
        using var services = Scopes(); using var worker = transport.Worker(registry, seed.Clock, services.GetRequiredService<IServiceScopeFactory>());
        await worker.ProcessBatchAsync(Ct);
        Assert.Empty(registry.PendingAccessChanges(8));
        Assert.Equal(!committed, registry.Contains(seed.Board.Id, seed.Guest.Id, "live-tab"));
        Assert.False(registry.Contains(seed.Board.Id, seed.Guest.Id, "closed-tab"));
        Assert.True(registry.Contains(seed.Board.Id, seed.Peer.Id, "peer"));
    }

    [Fact]
    public async Task Failed_reconciliation_keeps_exclusion_and_backs_off_database_retries()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var registry = new BoardConnectionRegistry(); var transport = new BoardTransportSpy();
        registry.Add(seed.Board.Id, seed.Guest.Id, "guest"); registry.BeginAccessChange(seed.Board.Id, [seed.Guest.Id]);
        using var services = Scopes(new FailReader()); using var worker = transport.Worker(registry, seed.Clock, services.GetRequiredService<IServiceScopeFactory>());
        await worker.ProcessBatchAsync(Ct);
        Assert.Empty(registry.GetConnections(seed.Board.Id, seed.Guest.Id)); Assert.Null(registry.PublicationRevision(seed.Board.Id));
        Assert.Single(registry.PendingAccessChanges(8));
        Assert.Empty(registry.PendingAccessChanges(8, seed.Clock.GetUtcNow()));
    }

    private ServiceProvider Scopes(params IInterceptor[] interceptors) => new ServiceCollection().AddDbContext<WuknaDbContext>(options =>
        options.UseNpgsql(postgres.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptors)).BuildServiceProvider();

    private sealed class FailReader : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default) => throw new IOException("test-only recipient validation failure");
    }
    private sealed class RecipientReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("board_memberships", StringComparison.Ordinal) && Interlocked.Increment(ref Reads) == 1)
            {
                Read.TrySetResult(); await Release.Task.WaitAsync(ct);
            }
            return result;
        }
    }
}
