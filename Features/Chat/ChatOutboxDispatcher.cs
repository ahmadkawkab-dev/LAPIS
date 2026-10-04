namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wukna.Shared.Data.AppDbContext;

public sealed class ChatOutboxOptions
{
    public bool Enabled { get; set; } = true;
    public int PollMilliseconds { get; set; } = 1000;
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 30;
    public int PublicationTimeoutSeconds { get; set; } = 5;
}

// Claim/acknowledgement are public to permit deterministic database integration
// tests and operational recovery without relying on a running polling loop.
public sealed class ChatOutboxDispatcher(IServiceScopeFactory scopes, IOptions<ChatOutboxOptions> options,
    TimeProvider clock, ILogger<ChatOutboxDispatcher> logger)
{
    public async Task<ChatOutboxEvent?> ClaimAsync(WuknaDbContext db, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.PublicationTimeoutSeconds));
        var now = clock.GetUtcNow();
        var token = Guid.NewGuid();
        var expiry = now.AddSeconds(options.Value.LeaseSeconds);
        var rows = await db.ChatOutboxEvents.FromSqlInterpolated($"""
            WITH due AS (
                SELECT id FROM chat_outbox_events
                WHERE processed_at IS NULL AND next_attempt_at <= {now}
                  AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
                ORDER BY next_attempt_at, created_at, id
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE chat_outbox_events AS work
            SET lease_token = {token}, lease_expires_at = {expiry}, attempts = work.attempts + 1
            FROM due WHERE work.id = due.id RETURNING work.*
            """).AsNoTracking().ToListAsync(deadline.Token);
        return rows.SingleOrDefault();
    }

    public static Task<int> AcknowledgeAsync(WuknaDbContext db, ChatOutboxEvent claim,
        DateTimeOffset now, CancellationToken ct) => claim.LeaseToken is null ? Task.FromResult(0) :
        db.ChatOutboxEvents.Where(work => work.Id == claim.Id &&
            work.ProcessedAt == null && work.LeaseToken == claim.LeaseToken && work.LeaseExpiresAt > now)
        .ExecuteUpdateAsync(set => set.SetProperty(work => work.ProcessedAt, now)
            .SetProperty(work => work.LeaseToken, (Guid?)null).SetProperty(work => work.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(work => work.LastErrorCode, (string?)null), ct);

    public async Task<bool> DispatchAsync(ChatOutboxEvent claim, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.PublicationTimeoutSeconds));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(deadline.Token);
            var now = clock.GetUtcNow();
            var owned = await db.ChatOutboxEvents.FromSqlInterpolated($"""
                SELECT * FROM chat_outbox_events WHERE id = {claim.Id} AND lease_token = {claim.LeaseToken}
                  AND processed_at IS NULL AND lease_expires_at > {now} FOR UPDATE
                """).AsNoTracking().ToListAsync(deadline.Token);
            if (owned.Count == 0) return false;
            await scope.ServiceProvider.GetRequiredService<IChatRealtimePublisher>().PublishAsync(db, owned[0], deadline.Token);
            if (await AcknowledgeAsync(db, claim, clock.GetUtcNow(), deadline.Token) != 1) return false;
            await transaction.CommitAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // A transport enqueue can succeed before acknowledgement fails. Retrying
            // uses the same event ID; clients must deduplicate and recover via HTTP.
            var code = exception is OperationCanceledException ? "publication_timeout" : exception.GetType().Name;
            logger.LogWarning("Chat outbox event {EventId}, board {BoardId}, attempt {Attempt} will retry: {FailureCode}",
                claim.Id, claim.BoardId, claim.Attempts, code);
            await using var recovery = scopes.CreateAsyncScope();
            var db = recovery.ServiceProvider.GetRequiredService<WuknaDbContext>();
            using var recoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recoveryDeadline.CancelAfter(TimeSpan.FromSeconds(options.Value.PublicationTimeoutSeconds));
            var next = clock.GetUtcNow().AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(claim.Attempts, 9))));
            await db.ChatOutboxEvents.Where(work => work.Id == claim.Id && work.ProcessedAt == null && work.LeaseToken == claim.LeaseToken)
                .ExecuteUpdateAsync(set => set.SetProperty(work => work.LeaseToken, (Guid?)null)
                    .SetProperty(work => work.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(work => work.NextAttemptAt, next).SetProperty(work => work.LastErrorCode, code), recoveryDeadline.Token);
            return false;
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var processed = 0;
        for (var index = 0; index < options.Value.BatchSize; index++)
        {
            ct.ThrowIfCancellationRequested();
            ChatOutboxEvent? claim;
            await using (var scope = scopes.CreateAsyncScope())
                claim = await ClaimAsync(scope.ServiceProvider.GetRequiredService<WuknaDbContext>(), ct);
            if (claim is null) break;
            if (await DispatchAsync(claim, ct)) processed++;
        }
        return processed;
    }
}

public sealed class ChatOutboxWorker(ChatOutboxDispatcher dispatcher, IOptions<ChatOutboxOptions> options,
    ILogger<ChatOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.PollMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var count = await dispatcher.ProcessBatchAsync(stoppingToken);
                    if (count > 0) logger.LogDebug("Published {Count} chat outbox events", count);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError("Chat outbox polling failed: {FailureType}", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
