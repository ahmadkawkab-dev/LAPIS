namespace Wukna.Features.Chat;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using Wukna.Features.Board;

public sealed class ChatOptions
{
    public int MessageSendsPerMinute { get; set; } = 30;
    public int ModerationWritesPerMinute { get; set; } = 30;
    public int ReadWritesPerMinute { get; set; } = 120;
}

public static class ChatEndpoints
{
    public static IServiceCollection AddChatMessages(this IServiceCollection services)
    {
        services.AddSingleton<ChatCursorCodec>();
        services.AddSingleton<ICalendarExportService, IcalNetCalendarExportService>();
        services.AddSingleton<ChatConnectionRegistry>();
        services.AddSingleton<ChatTypingRegistry>();
        services.AddSingleton<ChatTypingService>();
        services.AddSingleton<IChatRealtimePublisher, ChatRealtimePublisher>();
        services.AddSingleton<ChatOutboxDispatcher>();
        services.AddScoped<ChatRevocation>();
        services.AddHostedService<ChatOutboxWorker>();
        services.AddOptions<ChatAttachmentOptions>().BindConfiguration("Chat:Attachments")
            .Validate(options => options.MaxFileBytes is >= 1024 and <= 10 * 1024 * 1024 &&
                options.MaxImageBytes is >= 1024 and <= 16 * 1024 * 1024 && options.MaxPreviewBytes is >= 1024 and <= 1024 * 1024 &&
                options.BoardQuotaBytes is > 0 and <= 10L * 1024 * 1024 * 1024 && options.MemberQuotaBytes > 0 &&
                options.MemberQuotaBytes <= options.BoardQuotaBytes && options.MaxPendingPerBoard is >= 1 and <= 1000 &&
                options.MaxPendingPerMember >= 1 && options.MaxPendingPerMember <= options.MaxPendingPerBoard &&
                options.UploadsPerMinute is >= 1 and <= 100 && options.IoTimeoutSeconds is >= 1 and <= 60 &&
                options.JobTimeoutSeconds is >= 5 and <= 120 && options.LeaseSeconds >= options.JobTimeoutSeconds * 2 &&
                options.LeaseSeconds >= options.IoTimeoutSeconds * 2 && options.LeaseSeconds <= 600 &&
                options.ReservationSeconds >= options.IoTimeoutSeconds * 3 && options.ReservationSeconds <= 1800 &&
                options.PollMilliseconds is >= 100 and <= 60000 && options.BatchSize is >= 1 and <= 20 &&
                !string.IsNullOrWhiteSpace(options.ClamHost) && options.ClamPort is >= 1 and <= 65535 &&
                options.ClamTimeoutSeconds is >= 1 and <= 60 && !string.IsNullOrWhiteSpace(options.Directory),
                "Chat attachments require bounded sizes, quotas, backlog, timeouts, and expiring leases.")
            .Validate<IWebHostEnvironment>((options, environment) => !options.Enabled ||
                options.Provider == "Local" && environment.IsDevelopment() ||
                options.Provider == "Oci" && options.Oci.IsValid(),
                "Enabled attachments require development-only Local storage or explicitly configured private Oci storage.")
            .ValidateOnStart();
        services.AddSingleton<ChatImageValidation>();
        services.AddSingleton<OciChatAttachmentClients>();
        services.AddSingleton<IChatAttachmentStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ChatAttachmentOptions>>();
            if (!options.Value.Enabled) return new DisabledChatAttachmentStore();
            return options.Value.Provider == "Oci"
                ? new OciChatAttachmentStore(options, provider.GetRequiredService<OciChatAttachmentClients>())
                : new LocalChatAttachmentStore(options, provider.GetRequiredService<IWebHostEnvironment>(), provider.GetRequiredService<IConfiguration>());
        });
        services.AddSingleton<IAttachmentScanner, ClamAttachmentScanner>();
        services.AddSingleton<ChatAttachmentJobs>();
        services.AddHostedService<ChatAttachmentWorker>();
        services.AddOptions<ChatOutboxOptions>().BindConfiguration("Chat:Outbox")
            .Validate(options => options.PollMilliseconds is >= 100 and <= 60000 &&
                options.BatchSize is >= 1 and <= 100 && options.LeaseSeconds is >= 10 and <= 300 &&
                options.PublicationTimeoutSeconds is >= 1 and <= 30 &&
                options.LeaseSeconds >= options.PublicationTimeoutSeconds * 2,
                "Chat outbox requires bounded polling/batches and a lease at least twice the publication timeout.")
            .ValidateOnStart();
        services.AddOptions<ChatOptions>().BindConfiguration("Chat")
            .Validate(options => options.MessageSendsPerMinute is >= 1 and <= 10000 &&
                options.ModerationWritesPerMinute is >= 1 and <= 10000 && options.ReadWritesPerMinute is >= 1 and <= 10000,
                "Chat send/moderation/read limits must be between 1 and 10000.").ValidateOnStart();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("chat-send", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<ChatOptions>>().Value.MessageSendsPerMinute,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("chat-moderation", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<ChatOptions>>().Value.ModerationWritesPerMinute,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("chat-read", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<ChatOptions>>().Value.ReadWritesPerMinute,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("chat-upload", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<ChatAttachmentOptions>>().Value.UploadsPerMinute,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("board-membership-write", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptionsMonitor<BoardOptions>>().CurrentValue.MembershipWritesPerMinute,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.OnRejected = async (rejected, ct) =>
            {
                var response = rejected.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.CacheControl = "no-store";
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                var policy = rejected.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                rejected.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Wukna.RateLimit").LogWarning("Rate limit {Policy} rejected a request", policy);
                await response.WriteAsJsonAsync(new { error = policy == "board-membership-write" ? "board_membership_rate_limited" :
                    policy == "chat-moderation" ? "chat_moderation_rate_limited" :
                    policy == "chat-upload" ? "chat_upload_rate_limited" : "chat_rate_limited" }, ct);
            };
        });
        return services;
    }

    public static IEndpointRouteBuilder MapChatMessageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/boards/{boardId:guid}/chat").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/messages", ChatHistory.Page);
        group.MapGet("/state", ChatModeration.State);
        group.MapPut("/read", ChatRead.Update).Accepts<SetChatReadRequest>("application/json")
            .RequireRateLimiting("chat-read");
        group.MapGet("/members", ChatModeration.Members);
        group.MapPut("/settings", ChatModeration.Settings).RequireRateLimiting("chat-moderation");
        group.MapPut("/members/{memberId:guid}/mute", ChatModeration.Mute).RequireRateLimiting("chat-moderation");
        group.MapGet("/messages/{messageId:guid}", ChatHistory.Get);
        group.MapGet("/messages/{messageId:guid}/calendar.ics", ChatCalendarExport.Handle);
        group.MapPost("/messages", SendChatMessage.Handle)
            .Accepts<SendChatMessageRequest>("application/json").RequireRateLimiting("chat-send");
        group.MapPost("/scheduled-tasks", CreateScheduledChatTask.Handle)
            .Accepts<CreateScheduledChatTaskRequest>("application/json").RequireRateLimiting("chat-send");
        group.MapPost("/attachments", UploadChatAttachment.Handle).DisableAntiforgery().RequireRateLimiting("chat-upload");
        group.MapGet("/attachments/{attachmentId:guid}", DownloadChatAttachment.Handle);
        return endpoints;
    }
}
