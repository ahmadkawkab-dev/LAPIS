namespace Wukna.Features.Notifications;

using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wukna.Features.Chat;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;

public sealed class NotificationOptions { public bool WorkerEnabled { get; set; } = true; }
public sealed record NotificationChangedEvent(Guid EventId, Guid UserId, NotificationDto Notification);
public interface INotificationPushSender
{
    Task SendAsync(WuknaDbContext db, NotificationWork work, Notification notification, NotificationPreference preference, CancellationToken ct);
    Task QueueAsync(WuknaDbContext db, Notification notification, DateTimeOffset now, CancellationToken ct);
}

public sealed class NotificationDispatcher(IServiceScopeFactory scopes, TimeProvider clock,
    IHubContext<BoardHub> hub, INotificationPushSender push, IOptions<NotificationOptions> options,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        do
        {
            try { await ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError("Notification processing failed: {FailureType}", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ProcessAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
        var now = clock.GetUtcNow();
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var sources = await db.NotificationWork.FromSqlInterpolated($"""
                SELECT * FROM notification_work WHERE kind = 0 AND processed_at IS NULL AND next_attempt_at <= {now}
                ORDER BY created_at, id LIMIT 100 FOR UPDATE SKIP LOCKED
                """).ToArrayAsync(ct);
            foreach (var work in sources)
            {
                if (work.ExpiresAt > now) await GenerateAsync(db, work, now, ct);
                work.ProcessedAt = now;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        var lease = Guid.NewGuid();
        var claimed = await db.NotificationWork.FromSqlInterpolated($"""
            WITH pending AS (
              SELECT id FROM notification_work WHERE kind <> 0 AND processed_at IS NULL
                AND next_attempt_at <= {now} AND (lease_until IS NULL OR lease_until <= {now})
              ORDER BY next_attempt_at, created_at, id LIMIT 5 FOR UPDATE SKIP LOCKED
            ) UPDATE notification_work AS work SET lease_token = {lease}, lease_until = {now.AddSeconds(30)},
                attempts = attempts + 1 FROM pending WHERE work.id = pending.id RETURNING work.*
            """).AsNoTracking().ToArrayAsync(ct);
        foreach (var work in claimed)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (work.ExpiresAt > now) await DispatchAsync(db, work, now, timeout.Token);
                await db.NotificationWork.Where(item => item.Id == work.Id && item.LeaseToken == lease)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ProcessedAt, clock.GetUtcNow())
                        .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null).SetProperty(item => item.LeaseToken, (Guid?)null), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("Notification delivery {WorkId} failed: {FailureType}", work.Id, error.GetType().Name);
                var next = clock.GetUtcNow().AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(work.Attempts, 8))) + Random.Shared.NextDouble());
                await db.NotificationWork.Where(item => item.Id == work.Id && item.LeaseToken == lease)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAt, next)
                        .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null).SetProperty(item => item.LeaseToken, (Guid?)null), ct);
            }
            db.ChangeTracker.Clear();
        }
        // Bound durable deduplication history and presence storage, without deleting pending work.
        await db.NotificationWork.Where(item => item.ProcessedAt != null && item.ProcessedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        await db.NotificationClientPresence.Where(item => item.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
    }

    private static async Task GenerateAsync(WuknaDbContext db, NotificationWork work, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(item => item.Id == work.UserId, ct)) return;
        var preference = await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == work.UserId, ct)
            ?? new NotificationPreference();
        if (!Enabled(preference, work.Type)) return;
        var state = work.BoardId is null ? null : await db.BoardMemberChatStates.AsNoTracking().SingleOrDefaultAsync(item =>
            item.BoardId == work.BoardId && item.UserId == work.UserId && item.MembershipInstanceId == work.MembershipInstanceId, ct);
        if (work.BoardId is not null && state is null) return;
        var kind = work.ActivityKind;
        var title = "Board activity";
        if (work.BoardId is { } boardId)
            title = await db.Boards.Where(item => item.Id == boardId).Select(item => item.Title).SingleAsync(ct);
        if (work.Type == NotificationType.ChatActivity)
        {
            if (state!.LastReadSequence >= work.MessageSequence) return;
            var message = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == work.ResourceId && item.BoardId == work.BoardId, ct);
            if (message is null || message.SenderUserId == work.UserId ||
                message.Type == ChatMessageType.ScheduledTask && message.CreatedAt.AddHours(24) <= now) return;
            var mentions = JsonSerializer.Deserialize<ChatMentionDto[]>(message.MentionsJson) ?? [];
            var mentioned = mentions.Any(item => item.UserId == work.UserId);
            var replied = message.NotifyReplyAuthor && message.ReplyAuthorUserId == work.UserId;
            var boardPreference = await db.BoardNotificationPreferences.AsNoTracking().SingleOrDefaultAsync(item =>
                item.BoardId == work.BoardId && item.UserId == work.UserId, ct);
            if (boardPreference?.Mode == ChatNotificationMode.Muted || boardPreference?.MutedUntil > now ||
                boardPreference?.Mode == ChatNotificationMode.MentionsAndReplies && !mentioned && !replied) return;
            kind = mentioned ? "mention" : replied ? "reply" : kind;
            title = kind switch { "mention" => $"You were mentioned in {title}", "reply" => $"A reply in {title}",
                "scheduledTaskPosted" => $"Scheduled task shared in {title}", _ => $"New messages in {title}" };
        }
        else title = kind switch { "boardInvited" => $"You were invited to {title}", "taskCompleted" => $"Task completed in {title}",
            "taskReopened" => $"Task reopened in {title}", _ => $"Activity in {title}" };
        title = title.Length <= 500 ? title : title[..500];
        // Priority messages each retain a separate entry; ordinary activity groups in fixed, bounded windows.
        var priority = kind is "mention" or "reply" or "boardInvited";
        var bucket = work.CreatedAt.ToUnixTimeSeconds() / (work.Type == NotificationType.ChatActivity ? 30 : 60);
        var key = priority ? $"event:{work.SourceEventKey}" : $"{work.Type}:{work.BoardId:N}:{work.MembershipInstanceId:N}:{kind}:{bucket}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({work.UserId.ToString() + key}, 0))", ct);
        var chatSequence = work.Type == NotificationType.ChatActivity ? work.MessageSequence : null;
        if (work.Type == NotificationType.ChatActivity && chatSequence is not > 0)
            throw new InvalidOperationException("Chat notification work must have a positive message sequence.");
        var item = await db.Notifications.SingleOrDefaultAsync(item => item.UserId == work.UserId && item.AggregationKey == key && item.DismissedAt == null, ct);
        var newestChatMessage = true;
        if (item is null)
        {
            item = new Notification { UserId = work.UserId, Type = work.Type, BoardId = work.BoardId,
                MembershipInstanceId = work.MembershipInstanceId, AggregationKey = key,
                IssuedAt = work.CreatedAt, UpdatedAt = work.CreatedAt,
                FirstChatSequence = chatSequence, LastChatSequence = chatSequence };
            db.Notifications.Add(item);
        }
        else
        {
            if (chatSequence is { } sequence)
            {
                if (item.FirstChatSequence is not > 0 || item.LastChatSequence is not > 0)
                    throw new InvalidOperationException("Aggregated chat notification has no valid sequence range.");
                newestChatMessage = sequence >= item.LastChatSequence.Value;
                item.FirstChatSequence = Math.Min(item.FirstChatSequence.Value, sequence);
                item.LastChatSequence = Math.Max(item.LastChatSequence.Value, sequence);
                if (work.CreatedAt < item.IssuedAt) item.IssuedAt = work.CreatedAt;
                if (work.CreatedAt > item.UpdatedAt) item.UpdatedAt = work.CreatedAt;
            }
            else item.UpdatedAt = work.CreatedAt;
            item.Revision++; item.ActivityCount++; item.ReadAt = null;
        }
        // Work may arrive out of sequence after a retry or when messages share a timestamp.
        // The visible resource must describe the newest message in the stored sequence range.
        if (newestChatMessage)
        {
            item.Title = title; item.ActivityKind = kind; item.ActorUserId = work.ActorUserId;
            item.ResourceKind = work.ResourceKind; item.ResourceId = work.ResourceId;
        }
        NotificationSources.Deliver(db, item, now);
    }

    private async Task DispatchAsync(WuknaDbContext db, NotificationWork work, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(item => item.Id == work.UserId, ct)) return;
        if (work.Kind is NotificationWorkKind.StateChanged or NotificationWorkKind.PreferencesChanged)
        {
            await hub.Clients.User(work.UserId.ToString()).SendAsync(work.Kind == NotificationWorkKind.PreferencesChanged
                ? "NotificationPreferencesChanged" : "NotificationStateChanged", new { userId = work.UserId }, ct);
            return;
        }
        var notification = await NotificationEndpoints.Visible(db, work.UserId).AsNoTracking().Include(item => item.Task)
            .SingleOrDefaultAsync(item => item.Id == work.NotificationId, ct);
        if (notification is null || notification.Revision != work.NotificationRevision || notification.DismissedAt != null) return;
        var preference = await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == work.UserId, ct) ?? new NotificationPreference();
        if (!Enabled(preference, notification.Type)) return;
        if (notification.Type == NotificationType.ChatActivity)
        {
            var source = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == notification.ResourceId && item.BoardId == notification.BoardId, ct);
            if (source is null || source.Type == ChatMessageType.ScheduledTask && source.CreatedAt.AddHours(24) <= now ||
                await db.BoardMemberChatStates.AnyAsync(state => state.BoardId == notification.BoardId && state.UserId == work.UserId &&
                    state.MembershipInstanceId == notification.MembershipInstanceId && state.LastReadSequence >= notification.LastChatSequence, ct)) return;
            var boardPreference = await db.BoardNotificationPreferences.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == work.UserId && item.BoardId == notification.BoardId, ct);
            if (boardPreference?.Mode == ChatNotificationMode.Muted || boardPreference?.MutedUntil > now ||
                boardPreference?.Mode == ChatNotificationMode.MentionsAndReplies && notification.ActivityKind is not ("mention" or "reply")) return;
        }
        if (notification.Type == NotificationType.TaskReminder && !await db.TaskReminders.AnyAsync(item =>
            item.TaskId == notification.TaskId && item.UserId == work.UserId && item.DeliveredAt != null &&
            item.ScheduleGeneration == notification.ReminderGeneration && item.Task.CompletedAt == null, ct)) return;
        if (notification.Type == NotificationType.ScheduledTaskReminder && !await db.CalendarEventReminders.AnyAsync(item =>
            item.CalendarEventId == notification.ResourceId && item.UserId == work.UserId && item.DeliveredAt != null &&
            item.ScheduleGeneration == notification.ReminderGeneration, ct)) return;
        if (work.Kind == NotificationWorkKind.Push)
        {
            if (preference.PushEnabled && notification.ReadRevision < notification.Revision)
                await push.SendAsync(db, work, notification, preference, ct);
            return;
        }
        await push.QueueAsync(db, notification, now, ct);
        await hub.Clients.User(work.UserId.ToString()).SendAsync("NotificationChanged",
            new NotificationChangedEvent(work.Id, work.UserId, NotificationDto.From(notification)), ct);
    }

    internal static bool Enabled(NotificationPreference preference, NotificationType type) => type switch
    {
        NotificationType.ChatActivity => preference.ChatNotificationsEnabled,
        NotificationType.TaskReminder => preference.TaskReminderNotificationsEnabled,
        NotificationType.ScheduledTaskReminder => preference.ScheduledTaskReminderNotificationsEnabled,
        NotificationType.SharedBoardActivity => preference.SharedBoardNotificationsEnabled,
        NotificationType.BoardInvitation => preference.BoardInvitationNotificationsEnabled,
        NotificationType.TaskActivity => preference.TaskActivityNotificationsEnabled,
        _ => false
    };
}
