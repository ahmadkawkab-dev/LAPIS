namespace Wukna.Features.Chat;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wukna.Features.Users;

public sealed record SendChatMessageRequest(Guid ClientMessageId, string? Body)
{
    public static async ValueTask<SendChatMessageRequest?> BindAsync(HttpContext context, ParameterInfo _)
        => await ChatBoundedJson.ReadAsync<SendChatMessageRequest>(context);
}

internal static class ChatBoundedJson
{
    // Bound JSON bytes before deserialization, including chunked requests.
    internal static async ValueTask<T?> ReadAsync<T>(HttpContext context)
    {
        const int maxBytes = 32 * 1024;
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasJsonContentType())
            throw new BadHttpRequestException("Use application/json.", StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > maxBytes)
            throw new BadHttpRequestException("Chat request is too large.", StatusCodes.Status413PayloadTooLarge);
        using var json = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, maxBytes - (int)json.Length + 1)), context.RequestAborted);
            if (read == 0) break;
            if (json.Length + read > maxBytes)
                throw new BadHttpRequestException("Chat request is too large.", StatusCodes.Status413PayloadTooLarge);
            json.Write(buffer, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json.GetBuffer().AsSpan(0, (int)json.Length),
                context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                    .Value.SerializerOptions);
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Chat request must be valid JSON.", StatusCodes.Status400BadRequest);
        }
    }
}
public sealed record ChatSenderDto(Guid UserId, string Username, string? DisplayName,
    string? AvatarUrl, string? AvatarVersion)
{
    public static ChatSenderDto From(Wukna.Features.Users.User user) => new(user.Id, user.Username, user.DisplayName,
        ProfileImageUrls.For(user.ProfileImageKey, user.ProfileImageVersion), user.ProfileImageVersion);
}
public sealed record ChatAttachmentDto(Guid Id, string FileName, string ContentType, long ByteSize,
    int Width, int Height, string ScanStatus);
public sealed record ScheduledChatTaskDto(string Title, string? Description,
    DateTimeOffset StartsAtUtc, DateTimeOffset? EndsAtUtc, string TimeZoneId, int OriginalOffsetMinutes);
public sealed record ChatMessageDto(Guid Id, Guid BoardId, string Sequence, string Cursor,
    string Type, string? Body, DateTimeOffset CreatedAt, Guid ClientMessageId, ChatSenderDto Sender,
    ChatAttachmentDto? Attachment, ScheduledChatTaskDto? ScheduledTask)
{
    public static ChatMessageDto From(ChatMessage message, ChatCursorCodec cursors) => new(
        message.Id, message.BoardId, message.Sequence.ToString(CultureInfo.InvariantCulture),
        cursors.Encode(message.BoardId, message.Sequence), message.Type switch
        {
            ChatMessageType.Text => "text",
            ChatMessageType.Attachment => "attachment",
            ChatMessageType.ScheduledTask => "scheduledTask",
            _ => throw new InvalidOperationException("Unsupported persisted chat message type.")
        }, message.Body, message.CreatedAt, message.ClientMessageId,
        new ChatSenderDto(message.SenderUserId, message.SenderUser.Username,
            message.SenderUser.DisplayName,
            ProfileImageUrls.For(message.SenderUser.ProfileImageKey, message.SenderUser.ProfileImageVersion),
            message.SenderUser.ProfileImageVersion),
        message.Attachment is { } attachment ? new ChatAttachmentDto(attachment.Id,
            attachment.OriginalFileName, attachment.ContentType, attachment.ByteSize,
            attachment.Width, attachment.Height, attachment.ScanStatus.ToString()) : null,
        message.ScheduledTask is { } task ? new ScheduledChatTaskDto(task.Title, task.Description,
            task.StartsAtUtc, task.EndsAtUtc, task.TimeZoneId, task.OriginalOffsetMinutes) : null);
}
public sealed record ChatHistoryPageDto(IReadOnlyList<ChatMessageDto> Items,
    string? OlderCursor, string NewerCursor, bool HasMore, string CatchUpThrough, DateTimeOffset ServerTime);
public sealed record ChatSendResultDto(ChatMessageDto Message, DateTimeOffset ServerTime,
    DateTimeOffset? NextSendAllowedAt, bool IsReplay, long SettingsRevision);
