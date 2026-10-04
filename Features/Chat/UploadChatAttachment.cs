namespace Wukna.Features.Chat;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wukna.Shared.Data.AppDbContext;

public static class UploadChatAttachment
{
    public static async Task<IResult> Handle(Guid boardId, HttpContext context, WuknaDbContext db,
        IOptions<ChatAttachmentOptions> configured, ChatImageValidation images, IChatAttachmentStore store,
        ChatCursorCodec cursors, TimeProvider clock, IServiceScopeFactory scopes, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        if (!await db.BoardMemberships.AnyAsync(member => member.BoardId == boardId && member.UserId == userId, ct)) return Results.NotFound();
        var options = configured.Value;
        if (!options.Enabled) return Results.Json(new { error = "chat_attachments_disabled" }, statusCode: 503);
        var logger = logs.CreateLogger("Wukna.Features.Chat.Attachments");
        ChatBlobWork? reservation = null;
        var committed = false;
        try
        {
            var upload = await ReadAsync(context, options, ct);
            var digest = Convert.ToHexStringLower(SHA256.HashData(upload.Bytes));
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new { type = "attachment", upload.Name, upload.ContentType, upload.Body, digest }))));
            // Durable reservation precedes decoding and all private-store writes.
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, ct);
                if (settings is null) return Results.NotFound();
                var membership = await ChatAccess.LockMembershipAsync(db, boardId, userId, ct);
                if (membership is null) return Results.NotFound();
                var state = await ChatAccess.LockStateAsync(db, boardId, userId, ct);
                var now = clock.GetUtcNow();
                var previous = await ChatHistory.Messages(db, boardId).SingleOrDefaultAsync(message =>
                    message.SenderUserId == userId && message.ClientMessageId == upload.OperationId, ct);
                if (previous is not null)
                {
                    if (previous.RequestFingerprint != fingerprint) return Results.Conflict(new { error = "chat_operation_conflict" });
                    await transaction.CommitAsync(ct);
                    return Results.Ok(new ChatSendResultDto(ChatMessageDto.From(previous, cursors), now,
                        ChatSendPolicy.NextSend(settings, state, membership.Role), true, settings.SettingsRevision));
                }
                if (ChatSendPolicy.Rejection(context, settings, state, membership.Role, now) is { } rejection) return rejection;
                if (await db.ChatBlobWork.AnyAsync(work => work.BoardId == boardId && work.UserId == userId &&
                    work.ClientMessageId == upload.OperationId && work.Purpose == ChatBlobWorkPurpose.UploadReservation, ct))
                {
                    context.Response.Headers.RetryAfter = "2";
                    return Results.Conflict(new { error = "chat_upload_in_progress" });
                }
                var reservedBytes = upload.Bytes.LongLength + 2L * (options.MaxImageBytes + options.MaxPreviewBytes);
                var attachments = db.ChatAttachments.Where(attachment => attachment.Message.BoardId == boardId && attachment.ScanStatus != ChatAttachmentScanStatus.Rejected);
                var reservations = db.ChatBlobWork.Where(work => work.BoardId == boardId && work.Purpose == ChatBlobWorkPurpose.UploadReservation);
                var quotaWork = db.ChatBlobWork.Where(work => work.BoardId == boardId);
                var usedBoard = await attachments.SumAsync(attachment => (long?)attachment.StoredByteSize, ct) ?? 0;
                var usedMember = await attachments.Where(attachment => attachment.Message.SenderUserId == userId)
                    .SumAsync(attachment => (long?)attachment.StoredByteSize, ct) ?? 0;
                var reservedBoard = await quotaWork.SumAsync(work => (long?)work.ReservedBytes, ct) ?? 0;
                var reservedMember = await quotaWork.Where(work => work.UserId == userId).SumAsync(work => (long?)work.ReservedBytes, ct) ?? 0;
                if (reservedBytes > options.BoardQuotaBytes - usedBoard - reservedBoard ||
                    reservedBytes > options.MemberQuotaBytes - usedMember - reservedMember)
                    throw new ChatUploadValidationException("chat_attachment_quota", 429);
                var pending = attachments.Where(attachment => attachment.ScanStatus == ChatAttachmentScanStatus.Pending ||
                    attachment.ScanStatus == ChatAttachmentScanStatus.Scanning || attachment.ScanStatus == ChatAttachmentScanStatus.ScanFailed);
                if (await pending.CountAsync(ct) + await reservations.CountAsync(ct) >= options.MaxPendingPerBoard ||
                    await pending.CountAsync(attachment => attachment.Message.SenderUserId == userId, ct) +
                    await reservations.CountAsync(work => work.UserId == userId, ct) >= options.MaxPendingPerMember)
                    throw new ChatUploadValidationException("chat_scan_backlog", 429);
                reservation = new ChatBlobWork {
                    BoardId = boardId, UserId = userId, ClientMessageId = upload.OperationId, ReservedBytes = reservedBytes,
                    OriginalStorageKey = ChatAttachmentKeys.New(false), StorageKey = ChatAttachmentKeys.New(true), PreviewStorageKey = ChatAttachmentKeys.New(true),
                    CreatedAt = now, DueAt = now.AddSeconds(options.ReservationSeconds),
                    LeaseToken = Guid.NewGuid(), LeaseExpiresAt = now.AddSeconds(options.ReservationSeconds)
                };
                db.ChatBlobWork.Add(reservation);
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            var image = await images.NormalizeAsync(upload.Bytes, upload.ContentType, options, ct);
            db.ChangeTracker.Clear();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.IoTimeoutSeconds));
            var token = deadline.Token;
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var settings = await ChatAccess.LockSettingsAsync(db, boardId, userId, token);
                if (settings is null) return Results.NotFound();
                var membership = await ChatAccess.LockMembershipAsync(db, boardId, userId, token);
                if (membership is null) return Results.NotFound();
                var state = await ChatAccess.LockStateAsync(db, boardId, userId, token);
                var current = clock.GetUtcNow();
                var now = new DateTimeOffset(current.UtcTicks / 10 * 10, TimeSpan.Zero);
                if (ChatSendPolicy.Rejection(context, settings, state, membership.Role, now) is { } rejection) return rejection;
                var owned = await db.ChatBlobWork.FromSqlInterpolated($"""
                    SELECT * FROM chat_blob_work WHERE id = {reservation.Id} AND purpose = 0
                    AND lease_token = {reservation.LeaseToken} AND lease_expires_at > {now} FOR UPDATE
                    """).ToListAsync(token);
                if (owned.Count == 0) return Results.Conflict(new { error = "chat_upload_expired" });
                // Hold the reservation and board authorization through bounded I/O:
                // cleanup/removal/deletion cannot overtake an active writer.
                using var original = new MemoryStream(upload.Bytes, false);
                using var normalized = new MemoryStream(image.Image, false);
                using var preview = new MemoryStream(image.Preview, false);
                await store.PutQuarantineAsync(reservation.OriginalStorageKey, original, token);
                await store.PutQuarantineAsync(reservation.StorageKey, normalized, token);
                await store.PutQuarantineAsync(reservation.PreviewStorageKey, preview, token);
                var message = new ChatMessage {
                    BoardId = boardId, SenderUserId = userId, Type = ChatMessageType.Attachment, Body = upload.Body,
                    ClientMessageId = upload.OperationId, RequestFingerprint = fingerprint,
                    CreatedAt = now, Sequence = checked(++settings.LastMessageSequence),
                    SenderUser = await db.Users.SingleAsync(user => user.Id == userId, token)
                };
                message.Attachment = new ChatAttachment {
                    OriginalStorageKey = reservation.OriginalStorageKey, StorageKey = reservation.StorageKey, PreviewStorageKey = reservation.PreviewStorageKey,
                    OriginalFileName = upload.Name, InputByteSize = upload.Bytes.Length, ByteSize = image.Image.Length, PreviewByteSize = image.Preview.Length,
                    StoredByteSize = upload.Bytes.LongLength + 2L * (image.Image.Length + image.Preview.Length),
                    Width = image.Width, Height = image.Height, CreatedAt = now, NextScanAttemptAt = now
                };
                db.ChatMessages.Add(message);
                db.ChatOutboxEvents.Add(new ChatOutboxEvent { BoardId = boardId, Kind = ChatOutboxEventKind.MessageCreated,
                    MessageId = message.Id, MessageSequence = message.Sequence, CreatedAt = now, NextAttemptAt = now });
                ChatSendPolicy.Consume(settings, state, membership.Role, now);
                db.ChatBlobWork.Remove(owned[0]);
                await Wukna.Features.Notifications.NotificationSources.ChatAsync(db, message, now, token);
                await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
                committed = true;
                return Results.Created($"/api/boards/{boardId}/chat/messages/{message.Id}",
                    new ChatSendResultDto(ChatMessageDto.From(message, cursors), now, state.NextSendAllowedAt, false, settings.SettingsRevision));
            }
        }
        catch (ChatUploadValidationException failure)
        {
            logger.LogWarning("Chat attachment rejected on board {BoardId}, user {UserId}: {Code}", boardId, userId, failure.Code);
            return Results.Json(new { error = failure.Code }, statusCode: failure.Status);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Chat upload failed on board {BoardId}, user {UserId}: {FailureType}", boardId, userId, failure.GetType().Name);
            return Results.Json(new { error = "chat_upload_unavailable" }, statusCode: 503);
        }
        finally
        {
            if (reservation is not null && !committed)
            {
                // Cleanup remains durable even if this recovery update itself fails.
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(options.IoTimeoutSeconds));
                    await using var scope = scopes.CreateAsyncScope();
                    var recoveryDb = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
                    await recoveryDb.ChatBlobWork.Where(work => work.Id == reservation.Id && work.LeaseToken == reservation.LeaseToken)
                        .ExecuteUpdateAsync(set => set.SetProperty(work => work.DueAt, clock.GetUtcNow())
                            .SetProperty(work => work.LeaseToken, (Guid?)null).SetProperty(work => work.LeaseExpiresAt, (DateTimeOffset?)null), recovery.Token);
                }
                catch (Exception failure) { logger.LogWarning("Upload cleanup reservation {WorkId} retained: {FailureType}", reservation.Id, failure.GetType().Name); }
            }
        }
    }

    private sealed record Upload(Guid OperationId, string Name, string ContentType, string? Body, byte[] Bytes);
    private static async Task<Upload> ReadAsync(HttpContext context, ChatAttachmentOptions options, CancellationToken ct)
    {
        if (!context.Request.HasFormContentType || !context.Request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ChatUploadValidationException("chat_upload_type", 415);
        var limit = options.MaxFileBytes + 64 * 1024;
        if (context.Request.ContentLength > limit) throw new ChatUploadValidationException("chat_upload_too_large", 413);
        using var buffered = new MemoryStream();
        var block = new byte[81920];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(block.AsMemory(0, Math.Min(block.Length, limit - (int)buffered.Length + 1)), ct);
            if (read == 0) break;
            if (buffered.Length + read > limit) throw new ChatUploadValidationException("chat_upload_too_large", 413);
            buffered.Write(block, 0, read);
        }
        buffered.Position = 0;
        context.Request.Body = buffered;
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions {
            MultipartBodyLengthLimit = limit, ValueCountLimit = 3, ValueLengthLimit = 4096,
            MultipartBoundaryLengthLimit = 128, MultipartHeadersCountLimit = 16, MultipartHeadersLengthLimit = 8192,
            MemoryBufferThreshold = 64 * 1024
        }));
        IFormCollection form;
        try { form = await context.Request.ReadFormAsync(ct); }
        catch (InvalidDataException) { throw new ChatUploadValidationException("chat_invalid_upload"); }
        if (form.Files.Count != 1 || form.Files[0].Name != "file" || form.Keys.Any(key => key is not ("clientMessageId" or "body")) ||
            form["clientMessageId"].Count != 1 || !Guid.TryParse(form["clientMessageId"], out var operation) || operation == Guid.Empty || form["body"].Count > 1)
            throw new ChatUploadValidationException("chat_invalid_upload");
        var file = form.Files[0];
        if (file.Length < 1) throw new ChatUploadValidationException("chat_invalid_image");
        if (file.Length > options.MaxFileBytes) throw new ChatUploadValidationException("chat_upload_too_large", 413);
        var name = file.FileName.Replace('\\', '/').Split('/')[^1];
        name = new string(name.Where(character => !char.IsControl(character) && character is not ('\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069')).ToArray()).Trim();
        if (name.Length is < 1 or > 255) throw new ChatUploadValidationException("chat_filename");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var allowed = file.ContentType switch { "image/jpeg" => extension is ".jpg" or ".jpeg", "image/png" => extension == ".png", "image/webp" => extension == ".webp", _ => false };
        if (!allowed) throw new ChatUploadValidationException("chat_image_type", 415);
        var body = form["body"].ToString();
        if (body.Length > 2000) throw new ChatUploadValidationException("chat_invalid_caption");
        body = body.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        using var bytes = new MemoryStream((int)file.Length);
        await file.CopyToAsync(bytes, ct);
        return new(operation, name, file.ContentType, body.Length == 0 ? null : body, bytes.ToArray());
    }
}
