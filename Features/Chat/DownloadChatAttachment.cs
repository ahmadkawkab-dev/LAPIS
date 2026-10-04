namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Wukna.Shared.Data.AppDbContext;

public static class DownloadChatAttachment
{
    public static async Task<IResult> Handle(Guid boardId, Guid attachmentId, bool? preview, HttpContext context,
        WuknaDbContext db, IChatAttachmentStore store, IOptions<ChatAttachmentOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.IoTimeoutSeconds));
        var token = deadline.Token;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var boards = await db.Boards.FromSqlInterpolated($"SELECT * FROM boards WHERE id = {boardId} FOR KEY SHARE").AsNoTracking().ToListAsync(token);
        if (boards.Count == 0 || await ChatAccess.LockMembershipAsync(db, boardId, userId, token) is null) return Results.NotFound();
        var attachment = await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(attachment =>
            attachment.Id == attachmentId && attachment.Message.BoardId == boardId, token);
        if (attachment is null) return Results.NotFound();
        if (!options.Value.Enabled) return Results.Json(new { error = "chat_attachments_disabled" }, statusCode: 503);
        if (attachment.ScanStatus != ChatAttachmentScanStatus.Available)
            return Results.Conflict(new { error = "chat_attachment_unavailable", scanStatus = attachment.ScanStatus.ToString() });
        try
        {
            await using var stream = await store.OpenAsync(preview == true ? attachment.PreviewStorageKey : attachment.StorageKey, token);
            var length = preview == true ? attachment.PreviewByteSize : attachment.ByteSize;
            if (stream.CanSeek && stream.Length != length) throw new IOException("attachment_size_mismatch");
            context.Response.ContentType = "image/webp";
            context.Response.ContentLength = length;
            context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue(preview == true ? "inline" : "attachment") {
                FileNameStar = Path.GetFileNameWithoutExtension(attachment.OriginalFileName) + ".webp"
            }.ToString();
            // Keep current membership locked until the bounded transfer completes.
            await stream.CopyToAsync(context.Response.Body, token);
            await transaction.CommitAsync(token);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            logs.CreateLogger("Wukna.Features.Chat.Attachments").LogWarning("Download unavailable for attachment {AttachmentId}: {FailureType}", attachmentId, failure.GetType().Name);
            if (context.Response.HasStarted) { context.Abort(); return Results.Empty; }
            context.Response.ContentLength = null; context.Response.Headers.Remove(HeaderNames.ContentDisposition);
            return Results.Json(new { error = "chat_attachment_unavailable" }, statusCode: 503);
        }
    }
}
