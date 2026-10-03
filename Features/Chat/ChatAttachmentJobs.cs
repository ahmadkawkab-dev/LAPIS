namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wukna.Shared.Data.AppDbContext;

public sealed class ChatAttachmentJobs(IServiceScopeFactory scopes, IOptions<ChatAttachmentOptions> options,
    TimeProvider clock, ILogger<ChatAttachmentJobs> logger)
{
    public async Task<ChatAttachment?> ClaimScanAsync(WuknaDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var token = Guid.NewGuid(); var expiry = now.AddSeconds(options.Value.LeaseSeconds);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.ChatAttachments.FromSqlInterpolated($"""
            WITH due AS (
                SELECT id FROM chat_attachments WHERE scan_status IN (0, 1, 4) AND next_scan_attempt_at <= {now}
                  AND (scan_lease_expires_at IS NULL OR scan_lease_expires_at <= {now})
                ORDER BY next_scan_attempt_at, created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            ) UPDATE chat_attachments AS work SET scan_status = 1, scan_lease_token = {token},
                scan_lease_expires_at = {expiry}, scan_attempts = work.scan_attempts + 1
              FROM due WHERE work.id = due.id RETURNING work.*
            """).AsNoTracking().ToListAsync(ct);
        var claim = rows.SingleOrDefault();
        if (claim is not null)
        {
            var board = await db.ChatMessages.Where(message => message.Id == claim.MessageId).Select(message => message.BoardId).SingleAsync(ct);
            db.ChatOutboxEvents.Add(Changed(board, claim, now)); await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct); return claim;
    }

    public async Task<bool> ScanAsync(ChatAttachment claim, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.JobTimeoutSeconds));
        var token = deadline.Token;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IChatAttachmentStore>();
            var scanner = scope.ServiceProvider.GetRequiredService<IAttachmentScanner>();
            var infected = false;
            // Scan the unchanged original too: normalization is not malware scanning.
            foreach (var (key, length) in new[] { (claim.OriginalStorageKey, claim.InputByteSize),
                (ChatAttachmentKeys.Quarantine(claim.StorageKey), claim.ByteSize), (ChatAttachmentKeys.Quarantine(claim.PreviewStorageKey), claim.PreviewByteSize) })
            {
                await using var input = await store.OpenAsync(key, token);
                if (input.CanSeek && input.Length != length) throw new IOException("quarantine_size_mismatch");
                if (await scanner.ScanAsync(input, token) == AttachmentScanVerdict.Infected) { infected = true; break; }
            }
            var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
            var boardId = await db.ChatMessages.Where(message => message.Id == claim.MessageId).Select(message => (Guid?)message.BoardId).SingleOrDefaultAsync(token);
            if (boardId is null) return false;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // Match command lock order. Deletion cannot enqueue cleanup before promotion finishes.
            var boards = await db.Boards.FromSqlInterpolated($"SELECT * FROM boards WHERE id = {boardId} FOR KEY SHARE").AsNoTracking().ToListAsync(token);
            if (boards.Count == 0) return false;
            var now = clock.GetUtcNow();
            var owned = await db.ChatAttachments.FromSqlInterpolated($"""
                SELECT * FROM chat_attachments WHERE id = {claim.Id} AND scan_status = 1
                  AND scan_lease_token = {claim.ScanLeaseToken} AND scan_lease_expires_at > {now} FOR UPDATE
                """).ToListAsync(token);
            if (owned.Count == 0) return false;
            var attachment = owned[0];
            if (!infected)
            {
                await store.PromoteAsync(attachment.StorageKey, token);
                await store.PromoteAsync(attachment.PreviewStorageKey, token);
            }
            // Never let work that ran past its lease publish Available.
            if (attachment.ScanLeaseExpiresAt <= clock.GetUtcNow()) return false;
            attachment.ScanStatus = infected ? ChatAttachmentScanStatus.Rejected : ChatAttachmentScanStatus.Available;
            attachment.ScannedAt = clock.GetUtcNow(); attachment.ScanLeaseToken = null; attachment.ScanLeaseExpiresAt = null;
            attachment.LastErrorCode = infected ? "malware_detected" : null;
            db.ChatOutboxEvents.Add(Changed(boardId.Value, attachment, attachment.ScannedAt.Value));
            db.ChatBlobWork.Add(new ChatBlobWork {
                Purpose = ChatBlobWorkPurpose.Delete, BoardId = boardId.Value,
                UserId = infected ? await db.ChatMessages.Where(message => message.Id == claim.MessageId).Select(message => message.SenderUserId).SingleAsync(token) : null,
                OriginalStorageKey = attachment.OriginalStorageKey,
                StorageKey = infected ? attachment.StorageKey : ChatAttachmentKeys.Quarantine(attachment.StorageKey),
                PreviewStorageKey = infected ? attachment.PreviewStorageKey : ChatAttachmentKeys.Quarantine(attachment.PreviewStorageKey),
                ReservedBytes = infected ? attachment.StoredByteSize : 0,
                CreatedAt = attachment.ScannedAt.Value, DueAt = attachment.ScannedAt.Value
            });
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            if (infected) logger.LogWarning("Malware rejected for attachment {AttachmentId}, board {BoardId}", claim.Id, boardId);
            else logger.LogInformation("Attachment {AttachmentId} available on board {BoardId} after attempt {Attempt}", claim.Id, boardId, claim.ScanAttempts);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            var code = failure is OperationCanceledException ? "scan_timeout" : "scan_unavailable";
            logger.LogWarning("Attachment {AttachmentId} scan attempt {Attempt} failed closed: {Code}, {FailureType}",
                claim.Id, claim.ScanAttempts, code, failure.GetType().Name);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recovery.CancelAfter(TimeSpan.FromSeconds(options.Value.IoTimeoutSeconds));
            await using var transaction = await db.Database.BeginTransactionAsync(recovery.Token);
            var now = clock.GetUtcNow(); var next = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(claim.ScanAttempts, 9))));
            var changed = await db.ChatAttachments.Where(work => work.Id == claim.Id && work.ScanStatus == ChatAttachmentScanStatus.Scanning &&
                    work.ScanLeaseToken == claim.ScanLeaseToken && work.ScanLeaseExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(work => work.ScanStatus, ChatAttachmentScanStatus.ScanFailed)
                    .SetProperty(work => work.ScanLeaseToken, (Guid?)null).SetProperty(work => work.ScanLeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(work => work.NextScanAttemptAt, next).SetProperty(work => work.LastErrorCode, code), recovery.Token);
            if (changed == 1)
            {
                var board = await db.ChatMessages.Where(message => message.Id == claim.MessageId).Select(message => (Guid?)message.BoardId).SingleOrDefaultAsync(recovery.Token);
                if (board is { } id) { db.ChatOutboxEvents.Add(Changed(id, claim, now)); await db.SaveChangesAsync(recovery.Token); }
            }
            await transaction.CommitAsync(recovery.Token); return false;
        }
    }

    private static ChatOutboxEvent Changed(Guid boardId, ChatAttachment attachment, DateTimeOffset now) => new() {
        BoardId = boardId, Kind = ChatOutboxEventKind.AttachmentChanged, AttachmentId = attachment.Id,
        MessageId = attachment.MessageId, CreatedAt = now, NextAttemptAt = now
    };

    public async Task<ChatBlobWork?> ClaimCleanupAsync(WuknaDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var token = Guid.NewGuid(); var expiry = now.AddSeconds(options.Value.LeaseSeconds);
        var rows = await db.ChatBlobWork.FromSqlInterpolated($"""
            WITH due AS (
                SELECT id FROM chat_blob_work WHERE due_at <= {now}
                  AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
                ORDER BY due_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            ) UPDATE chat_blob_work AS work SET lease_token = {token}, lease_expires_at = {expiry}, attempts = work.attempts + 1
              FROM due WHERE work.id = due.id RETURNING work.*
            """).AsNoTracking().ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public async Task<bool> CleanupAsync(ChatBlobWork claim, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.IoTimeoutSeconds));
        var token = deadline.Token;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var now = clock.GetUtcNow();
            var owned = await db.ChatBlobWork.FromSqlInterpolated($"""
                SELECT * FROM chat_blob_work WHERE id = {claim.Id} AND lease_token = {claim.LeaseToken}
                  AND lease_expires_at > {now} FOR UPDATE
                """).ToListAsync(token);
            if (owned.Count == 0) return false;
            var store = scope.ServiceProvider.GetRequiredService<IChatAttachmentStore>();
            foreach (var key in new[] { claim.OriginalStorageKey, claim.StorageKey, claim.PreviewStorageKey })
                if (key.Length != 0) await store.DeleteAsync(key, token);
            if (owned[0].LeaseExpiresAt <= clock.GetUtcNow()) return false;
            db.ChatBlobWork.Remove(owned[0]); await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            logger.LogWarning("Attachment cleanup {WorkId} attempt {Attempt} failed: {FailureType}", claim.Id, claim.Attempts, failure.GetType().Name);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recovery.CancelAfter(TimeSpan.FromSeconds(options.Value.IoTimeoutSeconds));
            var now = clock.GetUtcNow(); var next = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(claim.Attempts, 9))));
            await db.ChatBlobWork.Where(work => work.Id == claim.Id && work.LeaseToken == claim.LeaseToken && work.LeaseExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(work => work.DueAt, next).SetProperty(work => work.LeaseToken, (Guid?)null)
                    .SetProperty(work => work.LeaseExpiresAt, (DateTimeOffset?)null).SetProperty(work => work.LastErrorCode, "cleanup_unavailable"), recovery.Token);
            return false;
        }
    }

    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        // One scan at a time per API; PostgreSQL leases arbitrate additional workers.
        for (var index = 0; index < options.Value.BatchSize; index++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
            var cleanup = await ClaimCleanupAsync(db, ct);
            if (cleanup is not null) await CleanupAsync(cleanup, ct);
            var scan = await ClaimScanAsync(db, ct);
            if (scan is not null) await ScanAsync(scan, ct);
            if (cleanup is null && scan is null) break;
        }
    }

    // Called under the board deletion lock and in its transaction, before cascades.
    public static Task<int> QueueBoardDeletionAsync(WuknaDbContext db, Guid boardId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_blob_work (id, purpose, board_id, original_storage_key, storage_key, preview_storage_key,
                reserved_bytes, created_at, due_at, attempts)
            SELECT gen_random_uuid(), 1, {boardId}, a.original_storage_key, a.storage_key, a.preview_storage_key,
                a.stored_byte_size, {now}, {now}, 0 FROM chat_attachments a
            JOIN chat_messages m ON m.id = a.message_id WHERE m.board_id = {boardId}
            """, ct);
}

public sealed class ChatAttachmentWorker(ChatAttachmentJobs jobs, IOptions<ChatAttachmentOptions> options,
    ILogger<ChatAttachmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.PollMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await jobs.ProcessBatchAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception failure) { logger.LogError("Chat attachment polling failed: {FailureType}", failure.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
