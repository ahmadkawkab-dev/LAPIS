namespace Wukna.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Wukna.Features.Chat;
using Wukna.Shared.Data.AppDbContext;

// Required settings make a full PUT explicit: omitted booleans cannot silently reset sound/category choices.
public sealed record NotificationSettings
{
    public required bool InAppEnabled { get; init; }
    public required bool PushEnabled { get; init; }
    public required bool SoundsMuted { get; init; }
    public required double SoundVolume { get; init; }
    public required bool ChatNotificationsEnabled { get; init; }
    public required bool TaskReminderNotificationsEnabled { get; init; }
    public required bool ScheduledTaskReminderNotificationsEnabled { get; init; }
    public required bool SharedBoardNotificationsEnabled { get; init; }
    public required bool BoardInvitationNotificationsEnabled { get; init; }
    public required bool TaskActivityNotificationsEnabled { get; init; }
    public required bool ChatSoundEnabled { get; init; }
    public required bool TaskReminderSoundEnabled { get; init; }
    public required bool ScheduledTaskPostedSoundEnabled { get; init; }
    public required bool BoardInvitationSoundEnabled { get; init; }
    public required bool TaskCompletedSoundEnabled { get; init; }
    public required bool PrivatePreviewsEnabled { get; init; }

    public static NotificationSettings From(NotificationPreference item) => new()
    {
        InAppEnabled = item.InAppEnabled, PushEnabled = item.PushEnabled, SoundsMuted = item.SoundsMuted,
        SoundVolume = item.SoundVolume, ChatNotificationsEnabled = item.ChatNotificationsEnabled,
        TaskReminderNotificationsEnabled = item.TaskReminderNotificationsEnabled,
        ScheduledTaskReminderNotificationsEnabled = item.ScheduledTaskReminderNotificationsEnabled,
        SharedBoardNotificationsEnabled = item.SharedBoardNotificationsEnabled,
        BoardInvitationNotificationsEnabled = item.BoardInvitationNotificationsEnabled,
        TaskActivityNotificationsEnabled = item.TaskActivityNotificationsEnabled,
        ChatSoundEnabled = item.ChatSoundEnabled, TaskReminderSoundEnabled = item.TaskReminderSoundEnabled,
        ScheduledTaskPostedSoundEnabled = item.ScheduledTaskPostedSoundEnabled,
        BoardInvitationSoundEnabled = item.BoardInvitationSoundEnabled,
        TaskCompletedSoundEnabled = item.TaskCompletedSoundEnabled, PrivatePreviewsEnabled = item.PrivatePreviewsEnabled
    };
}

public sealed record NotificationPreferenceDto(NotificationSettings Settings, long Revision, DateTimeOffset? UpdatedAt);
public sealed record NotificationPreferenceWriteRequest(long Revision, NotificationSettings Settings);
public sealed record BoardNotificationPreferenceDto(string Mode, string EffectiveMode, bool SoundsMuted,
    DateTimeOffset? MutedUntil, long Revision, DateTimeOffset? UpdatedAt);
public sealed record BoardNotificationPreferenceWriteRequest(string Mode, bool SoundsMuted,
    DateTimeOffset? MutedUntil, long Revision);

public static class NotificationPreferenceEndpoints
{
    public static IEndpointRouteBuilder MapNotificationPreferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var global = endpoints.MapGroup("/api/notifications/preferences").RequireAuthorization();
        global.MapGet("/", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!await db.Users.AnyAsync(item => item.Id == userId, ct)) return Results.NotFound();
            var item = await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, ct);
            return Results.Ok(ToDto(item ?? new NotificationPreference()));
        });
        global.MapPut("/", async (NotificationPreferenceWriteRequest request, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var settings = request.Settings;
            if (request.Revision < 0 || settings is null || !double.IsFinite(settings.SoundVolume) ||
                settings.SoundVolume is < 0 or > 1) return BadRequest("invalid_notification_preferences");
            if (!await db.Users.AnyAsync(item => item.Id == userId, ct)) return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            // The expected revision also protects concurrent first writes; only revision zero may insert.
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_preferences
                  (user_id, in_app_enabled, push_enabled, sounds_muted, sound_volume,
                   chat_notifications_enabled, task_reminder_notifications_enabled,
                   scheduled_task_reminder_notifications_enabled, shared_board_notifications_enabled,
                   board_invitation_notifications_enabled, task_activity_notifications_enabled,
                   chat_sound_enabled, task_reminder_sound_enabled, scheduled_task_posted_sound_enabled,
                   board_invitation_sound_enabled, task_completed_sound_enabled, private_previews_enabled,
                   revision, updated_at)
                SELECT {userId}, {settings.InAppEnabled}, {settings.PushEnabled}, {settings.SoundsMuted},
                   {settings.SoundVolume}, {settings.ChatNotificationsEnabled}, {settings.TaskReminderNotificationsEnabled},
                   {settings.ScheduledTaskReminderNotificationsEnabled}, {settings.SharedBoardNotificationsEnabled},
                   {settings.BoardInvitationNotificationsEnabled}, {settings.TaskActivityNotificationsEnabled},
                   {settings.ChatSoundEnabled}, {settings.TaskReminderSoundEnabled}, {settings.ScheduledTaskPostedSoundEnabled},
                   {settings.BoardInvitationSoundEnabled}, {settings.TaskCompletedSoundEnabled}, {settings.PrivatePreviewsEnabled},
                   1, {now}
                WHERE {request.Revision} = 0 OR EXISTS (
                    SELECT 1 FROM notification_preferences WHERE user_id = {userId})
                ON CONFLICT (user_id) DO UPDATE SET
                   in_app_enabled = EXCLUDED.in_app_enabled, push_enabled = EXCLUDED.push_enabled,
                   sounds_muted = EXCLUDED.sounds_muted, sound_volume = EXCLUDED.sound_volume,
                   chat_notifications_enabled = EXCLUDED.chat_notifications_enabled,
                   task_reminder_notifications_enabled = EXCLUDED.task_reminder_notifications_enabled,
                   scheduled_task_reminder_notifications_enabled = EXCLUDED.scheduled_task_reminder_notifications_enabled,
                   shared_board_notifications_enabled = EXCLUDED.shared_board_notifications_enabled,
                   board_invitation_notifications_enabled = EXCLUDED.board_invitation_notifications_enabled,
                   task_activity_notifications_enabled = EXCLUDED.task_activity_notifications_enabled,
                   chat_sound_enabled = EXCLUDED.chat_sound_enabled,
                   task_reminder_sound_enabled = EXCLUDED.task_reminder_sound_enabled,
                   scheduled_task_posted_sound_enabled = EXCLUDED.scheduled_task_posted_sound_enabled,
                   board_invitation_sound_enabled = EXCLUDED.board_invitation_sound_enabled,
                   task_completed_sound_enabled = EXCLUDED.task_completed_sound_enabled,
                   private_previews_enabled = EXCLUDED.private_previews_enabled,
                   revision = notification_preferences.revision + 1, updated_at = EXCLUDED.updated_at
                WHERE notification_preferences.revision = {request.Revision}
                """, ct);
            if (updated == 0) return Conflict();
            var item = await db.NotificationPreferences.AsNoTracking().SingleAsync(item => item.UserId == userId, ct);
            NotificationSources.StateChanged(db, userId, now, preferences: true);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(ToDto(item));
        });

        var board = endpoints.MapGroup("/api/boards/{boardId:guid}/notification-preferences").RequireAuthorization();
        board.MapGet("/", async (Guid boardId, HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!await db.BoardMemberships.AnyAsync(item => item.BoardId == boardId && item.UserId == userId, ct))
                return Results.NotFound();
            var item = await db.BoardNotificationPreferences.AsNoTracking().SingleOrDefaultAsync(
                item => item.BoardId == boardId && item.UserId == userId, ct);
            return Results.Ok(ToDto(item ?? new BoardNotificationPreference(), clock.GetUtcNow()));
        });
        board.MapPut("/", async (Guid boardId, BoardNotificationPreferenceWriteRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!NotificationEndpoints.TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var now = clock.GetUtcNow();
            if (!TryMode(request.Mode, out var mode) || request.Revision < 0 ||
                request.MutedUntil is { } until && (until <= now || until > now.AddDays(7)))
                return BadRequest("invalid_board_notification_preferences");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Match board deletion's lock ordering, then hold membership throughout the preference write.
            var boards = await db.Boards.FromSqlInterpolated($"SELECT * FROM boards WHERE id = {boardId} FOR KEY SHARE")
                .AsNoTracking().ToArrayAsync(ct);
            if (boards.Length == 0 || await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null)
                return Results.NotFound();
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO board_notification_preferences
                  (board_id, user_id, mode, sounds_muted, muted_until, revision, updated_at)
                SELECT {boardId}, {userId}, {(int)mode}, {request.SoundsMuted}, {request.MutedUntil}, 1, {now}
                WHERE {request.Revision} = 0 OR EXISTS (
                    SELECT 1 FROM board_notification_preferences WHERE board_id = {boardId} AND user_id = {userId})
                ON CONFLICT (board_id, user_id) DO UPDATE SET mode = EXCLUDED.mode,
                  sounds_muted = EXCLUDED.sounds_muted, muted_until = EXCLUDED.muted_until,
                  revision = board_notification_preferences.revision + 1, updated_at = EXCLUDED.updated_at
                WHERE board_notification_preferences.revision = {request.Revision}
                """, ct);
            if (updated == 0) return Conflict();
            var item = await db.BoardNotificationPreferences.AsNoTracking().SingleAsync(
                item => item.BoardId == boardId && item.UserId == userId, ct);
            NotificationSources.StateChanged(db, userId, now, preferences: true);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(ToDto(item, now));
        });
        return endpoints;
    }

    private static NotificationPreferenceDto ToDto(NotificationPreference item) =>
        new(NotificationSettings.From(item), item.Revision, item.Revision == 0 ? null : item.UpdatedAt);
    private static BoardNotificationPreferenceDto ToDto(BoardNotificationPreference item, DateTimeOffset now) =>
        new(ModeName(item.Mode), ModeName(item.MutedUntil > now ? ChatNotificationMode.Muted : item.Mode),
            item.SoundsMuted, item.MutedUntil, item.Revision, item.Revision == 0 ? null : item.UpdatedAt);
    private static string ModeName(ChatNotificationMode mode) => mode switch
    {
        ChatNotificationMode.AllActivity => "allActivity",
        ChatNotificationMode.MentionsAndReplies => "mentionsAndReplies",
        ChatNotificationMode.Muted => "muted",
        _ => throw new InvalidOperationException("Unknown chat notification mode.")
    };
    private static bool TryMode(string value, out ChatNotificationMode mode)
    {
        mode = value switch
        {
            "allActivity" => ChatNotificationMode.AllActivity,
            "mentionsAndReplies" => ChatNotificationMode.MentionsAndReplies,
            "muted" => ChatNotificationMode.Muted,
            _ => (ChatNotificationMode)(-1)
        };
        return Enum.IsDefined(mode);
    }
    private static IResult Conflict() => Results.Conflict(new { code = "notification_preferences_changed" });
    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
}
