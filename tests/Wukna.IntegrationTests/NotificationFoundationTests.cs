namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wukna.Features.Chat;
using Wukna.Features.Notifications;
using Wukna.Features.Tasks;
using Xunit;
using static ChatControlTestSupport;

public sealed class NotificationFoundationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Account_preferences_are_authenticated_persistent_and_revision_protected()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest);
        using var other = Client(factory, seed.Outsider);
        using var anonymous = factory.CreateClient();
        const string path = "/api/notifications/preferences";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path, ct)).StatusCode);
        var initial = (await guest.GetFromJsonAsync<NotificationPreferenceDto>(path, ct))!;
        Assert.Equal(0, initial.Revision);
        Assert.True(initial.Settings.InAppEnabled);
        Assert.False(initial.Settings.PushEnabled);
        Assert.False(initial.Settings.SoundsMuted);
        Assert.Equal(0.5, initial.Settings.SoundVolume);
        Assert.True(initial.Settings.ChatSoundEnabled);
        await using (var db = postgres.CreateContext()) Assert.Empty(await db.NotificationPreferences.ToArrayAsync(ct));

        var settings = initial.Settings with { SoundsMuted = true, TaskCompletedSoundEnabled = false, SoundVolume = 0.25 };
        using var saved = await guest.PutAsJsonAsync(path, new { settings, revision = 0, userId = seed.Outsider.Id }, ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("no-store", saved.Headers.CacheControl!.ToString());
        var persisted = (await saved.Content.ReadFromJsonAsync<NotificationPreferenceDto>(ct))!;
        Assert.Equal(1, persisted.Revision);
        Assert.Equal(settings, persisted.Settings);
        using var anotherSession = Client(factory, seed.Guest);
        Assert.Equal(persisted, await anotherSession.GetFromJsonAsync<NotificationPreferenceDto>(path, ct));
        Assert.Equal(initial, await other.GetFromJsonAsync<NotificationPreferenceDto>(path, ct));
        Assert.Equal(HttpStatusCode.Conflict, (await guest.PutAsJsonAsync(path,
            new NotificationPreferenceWriteRequest(0, initial.Settings), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PutAsJsonAsync(path,
            new NotificationPreferenceWriteRequest(1, settings with { SoundVolume = 1.01 }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PutAsJsonAsync(path,
            new { revision = 1, settings = new { soundsMuted = false } }, ct)).StatusCode);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => guest.PutAsJsonAsync(path,
            new NotificationPreferenceWriteRequest(1, settings with { SoundVolume = index / 10.0 }), ct)));
        try
        {
            Assert.Single(concurrent, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(5, concurrent.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }
        finally { foreach (var response in concurrent) response.Dispose(); }
        Assert.Equal(2, (await guest.GetFromJsonAsync<NotificationPreferenceDto>(path, ct))!.Revision);
        await using (var db = postgres.CreateContext())
        {
            Assert.Equal(seed.Guest.Id, (await db.NotificationPreferences.SingleAsync(ct)).UserId);
        }
        var firstWrites = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => other.PutAsJsonAsync(path,
            new NotificationPreferenceWriteRequest(0, settings), ct)));
        try
        {
            Assert.Single(firstWrites, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(3, firstWrites.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }
        finally { foreach (var response in firstWrites) response.Dispose(); }
    }

    [Fact]
    public async Task Board_preferences_require_membership_expire_temporary_mutes_and_reset_on_reinvitation()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var guest = Client(factory, seed.Guest);
        using var outsider = Client(factory, seed.Outsider);
        var path = $"/api/boards/{seed.Board.Id}/notification-preferences";
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path, ct)).StatusCode);
        var initial = (await guest.GetFromJsonAsync<BoardNotificationPreferenceDto>(path, ct))!;
        Assert.Equal("allActivity", initial.Mode);
        var state = await State(guest, seed, ct);
        Assert.Equal(HttpStatusCode.OK, (await Mute(owner, seed, seed.Guest, state, true, ct)).StatusCode);
        var until = seed.Clock.GetUtcNow().AddHours(1);
        using var saved = await guest.PutAsJsonAsync(path,
            new { mode = "mentionsAndReplies", soundsMuted = true, mutedUntil = until, revision = 0, userId = seed.Owner.Id }, ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var current = (await saved.Content.ReadFromJsonAsync<BoardNotificationPreferenceDto>(ct))!;
        Assert.Equal("mentionsAndReplies", current.Mode);
        Assert.Equal("muted", current.EffectiveMode);
        Assert.True((await State(guest, seed, ct)).IsMuted);
        Assert.Equal(HttpStatusCode.Conflict, (await guest.PutAsJsonAsync(path,
            new BoardNotificationPreferenceWriteRequest("muted", false, null, 0), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PutAsJsonAsync(path,
            new BoardNotificationPreferenceWriteRequest("everyone", false, null, 1), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(path,
            new BoardNotificationPreferenceWriteRequest("muted", false, null, 0), ct)).StatusCode);
        seed.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal("mentionsAndReplies", (await guest.GetFromJsonAsync<BoardNotificationPreferenceDto>(path, ct))!.EffectiveMode);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, seed, seed.Guest, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PutAsJsonAsync(path,
            new BoardNotificationPreferenceWriteRequest("muted", false, null, 0), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Invite(owner, seed, seed.Guest, ct)).StatusCode);
        Assert.Equal(initial, await guest.GetFromJsonAsync<BoardNotificationPreferenceDto>(path, ct));
        Assert.False((await State(guest, seed, ct)).IsMuted);
    }

    [Fact]
    public async Task Generic_notifications_enforce_ownership_membership_incarnation_and_observed_read_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var guest = Client(factory, seed.Guest);
        using var other = Client(factory, seed.Outsider);
        using var anonymous = factory.CreateClient();
        var state = await State(guest, seed, ct);
        var item = new Notification { UserId = seed.Guest.Id, Type = NotificationType.ChatActivity,
            Title = "New chat activity", BoardId = seed.Board.Id, MembershipInstanceId = state.MembershipInstanceId,
            ResourceKind = "chatMessage", ResourceId = Guid.NewGuid(), Revision = 3, ReadRevision = 1,
            IssuedAt = seed.Clock.GetUtcNow(), UpdatedAt = seed.Clock.GetUtcNow(), ReadAt = seed.Clock.GetUtcNow() };
        var foreign = new Notification { UserId = seed.Outsider.Id, Type = NotificationType.TaskActivity,
            Title = "Private activity", IssuedAt = seed.Clock.GetUtcNow(), UpdatedAt = seed.Clock.GetUtcNow() };
        await using (var db = postgres.CreateContext())
        {
            db.Notifications.AddRange(item, foreign); await db.SaveChangesAsync(ct);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications/unread-count", ct)).StatusCode);
        var page = (await guest.GetFromJsonAsync<NotificationPageDto>("/api/notifications/page", ct))!;
        Assert.Equal(item.Id, Assert.Single(page.Items).Id);
        Assert.Equal("chatActivity", page.Items[0].Type);
        Assert.Null(page.Items[0].TaskId);
        Assert.True(page.Items[0].IsUnread);
        Assert.Equal(1, (await guest.GetFromJsonAsync<NotificationUnreadCountDto>("/api/notifications/unread-count", ct))!.UnreadCount);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/notifications/{item.Id}/read?revision=3", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync($"/api/notifications/{item.Id}/read", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsync($"/api/notifications/{item.Id}/read?revision=2", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsync($"/api/notifications/{item.Id}/read?revision=1", null, ct)).StatusCode);
        Assert.Equal(2, (await guest.GetFromJsonAsync<NotificationPageDto>("/api/notifications/page", ct))!.Items[0].ReadRevision);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync($"/api/notifications/{item.Id}/dismiss?revision=2", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsync("/api/notifications/read-all", null, ct)).StatusCode);
        Assert.Equal(0, (await guest.GetFromJsonAsync<NotificationUnreadCountDto>("/api/notifications/unread-count", ct))!.UnreadCount);
        Assert.Equal(1, (await other.GetFromJsonAsync<NotificationUnreadCountDto>("/api/notifications/unread-count", ct))!.UnreadCount);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, seed, seed.Guest, ct)).StatusCode);
        Assert.Equal(0, (await guest.GetFromJsonAsync<NotificationPageDto>("/api/notifications/page", ct))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync($"/api/notifications/{item.Id}/read?revision=3", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Invite(owner, seed, seed.Guest, ct)).StatusCode);
        var rejoined = await State(guest, seed, ct);
        Assert.NotEqual(state.MembershipInstanceId, rejoined.MembershipInstanceId);
        Assert.Equal(0, (await guest.GetFromJsonAsync<NotificationPageDto>("/api/notifications/page", ct))!.TotalCount);
    }

    [Fact]
    public async Task Dismiss_read_clears_all_accessible_read_entries_and_preserves_unread_foreign_and_obsolete_membership_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest);
        using var anonymous = factory.CreateClient();
        var state = await State(guest, seed, ct);
        var now = seed.Clock.GetUtcNow();
        Notification Read(Guid userId) => new()
        {
            UserId = userId, Type = NotificationType.TaskActivity, Title = "Read activity",
            IssuedAt = now, UpdatedAt = now, Revision = 2, ReadRevision = 2, ReadAt = now.AddMinutes(-1)
        };
        var read = Enumerable.Range(0, 40).Select(_ => Read(seed.Guest.Id)).ToArray();
        var boardRead = Read(seed.Guest.Id);
        boardRead.BoardId = seed.Board.Id; boardRead.MembershipInstanceId = state.MembershipInstanceId;
        var stale = Read(seed.Guest.Id);
        stale.BoardId = seed.Board.Id; stale.MembershipInstanceId = Guid.NewGuid();
        var foreign = Read(seed.Outsider.Id);
        var partiallyRead = Read(seed.Guest.Id);
        partiallyRead.Revision = 3; // ReadAt alone does not make the latest activity read.
        var unread = Read(seed.Guest.Id);
        unread.BoardId = seed.Board.Id; unread.MembershipInstanceId = state.MembershipInstanceId;
        unread.ReadRevision = 0; unread.ReadAt = null;
        var dismissed = Read(seed.Guest.Id);
        dismissed.DismissedAt = now.AddHours(-1);
        await using (var db = postgres.CreateContext())
        {
            db.Notifications.AddRange(read);
            db.Notifications.AddRange(boardRead, stale, foreign, partiallyRead, unread, dismissed);
            await db.SaveChangesAsync(ct);
        }
        const string path = "/api/notifications/dismiss-read";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, null, ct)).StatusCode);
        var before = (await guest.GetFromJsonAsync<NotificationPageDto>("/api/notifications/page", ct))!;
        Assert.Equal(30, before.Items.Count);
        Assert.NotNull(before.NextCursor);
        // Ownership always comes from authentication, even if a client sends an unrelated ID.
        using var response = await guest.PostAsJsonAsync(path, new { userId = seed.Outsider.Id }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(41, (await response.Content.ReadFromJsonAsync<DismissReadNotificationsDto>(ct))!.DismissedCount);
        var after = (await guest.GetFromJsonAsync<NotificationPageDto>("/api/notifications/page", ct))!;
        Assert.Equal(2, after.TotalCount); Assert.Equal(2, after.UnreadCount);
        Assert.All(after.Items, item => Assert.True(item.IsUnread));
        using var repeated = await guest.PostAsync(path, null, ct);
        Assert.Equal(0, (await repeated.Content.ReadFromJsonAsync<DismissReadNotificationsDto>(ct))!.DismissedCount);
        await using (var db = postgres.CreateContext())
        {
            var saved = await db.Notifications.AsNoTracking().ToDictionaryAsync(item => item.Id, ct);
            Assert.All(read.Append(boardRead), item =>
            {
                Assert.Equal(now, saved[item.Id].DismissedAt);
                Assert.Equal(item.ReadAt, saved[item.Id].ReadAt);
            });
            Assert.All(new[] { stale, foreign, partiallyRead, unread }, item => Assert.Null(saved[item.Id].DismissedAt));
            Assert.Equal(dismissed.DismissedAt, saved[dismissed.Id].DismissedAt);
            Assert.Single(await db.NotificationWork.Where(item => item.Kind == NotificationWorkKind.StateChanged && item.UserId == seed.Guest.Id).ToArrayAsync(ct));
        }
    }

    [Fact]
    public async Task Reminder_category_disable_prevents_generation_but_sound_mute_does_not()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        var due = seed.Clock.GetUtcNow().AddHours(1);
        var muted = new PersonalTask { UserId = seed.Guest.Id, Title = "Muted sound", CreatedAt = due, UpdatedAt = due };
        var disabled = new PersonalTask { UserId = seed.Peer.Id, Title = "Disabled category", CreatedAt = due, UpdatedAt = due };
        await using (var db = postgres.CreateContext())
        {
            db.PersonalTasks.AddRange(muted, disabled);
            db.TaskReminders.AddRange(new TaskReminder { TaskId = muted.Id, UserId = muted.UserId, DueAtUtc = due, CreatedAt = due, UpdatedAt = due },
                new TaskReminder { TaskId = disabled.Id, UserId = disabled.UserId, DueAtUtc = due, CreatedAt = due, UpdatedAt = due });
            db.NotificationPreferences.AddRange(new NotificationPreference { UserId = seed.Guest.Id, SoundsMuted = true, Revision = 1, UpdatedAt = due },
                new NotificationPreference { UserId = seed.Peer.Id, TaskReminderNotificationsEnabled = false, Revision = 1, UpdatedAt = due });
            await db.SaveChangesAsync(ct);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = postgres.CreateContext(); return await TaskReminderWorker.ProcessDue(db, due, ct);
        }));
        Assert.Equal(1, results.Sum());
        await using (var db = postgres.CreateContext())
        {
            var notification = Assert.Single(await db.Notifications.ToArrayAsync(ct));
            Assert.Equal(muted.Id, notification.Id);
            Assert.Equal(muted.Title, notification.Title);
            Assert.All(await db.TaskReminders.ToArrayAsync(ct), reminder => Assert.NotNull(reminder.DeliveredAt));
            Assert.Equal(0, await TaskReminderWorker.ProcessDue(db, due, ct));
        }
    }

    [Fact]
    public async Task Migration_preserves_legacy_notification_ids_read_and_dismissed_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        await using var db = postgres.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261003214259_AddChatTaskCalendarImports", ct);
        var owner = User("legacy-notification");
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var tasks = Enumerable.Range(0, 3).Select(index => new PersonalTask { UserId = owner.Id,
            Title = $"Legacy {index}", CreatedAt = now, UpdatedAt = now }).ToArray();
        db.Users.Add(owner); db.PersonalTasks.AddRange(tasks); await db.SaveChangesAsync(ct);
        for (var index = 0; index < tasks.Length; index++)
        {
            DateTimeOffset? read = index > 0 ? now : null;
            DateTimeOffset? dismissed = index == 2 ? now : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO task_notifications (task_id, user_id, issued_at, read_at, dismissed_at)
                VALUES ({tasks[index].Id}, {owner.Id}, {now}, {read}, {dismissed})
                """, ct);
        }
        await migrator.MigrateAsync(cancellationToken: ct);
        var notifications = await db.Notifications.AsNoTracking().OrderBy(item => item.Title).ToArrayAsync(ct);
        Assert.Equal(3, notifications.Length);
        for (var index = 0; index < tasks.Length; index++)
        {
            Assert.Equal(tasks[index].Id, notifications[index].Id);
            Assert.Equal(tasks[index].Id, notifications[index].TaskId);
            Assert.Equal(owner.Id, notifications[index].UserId);
            Assert.Equal(now, notifications[index].IssuedAt);
            Assert.Equal(index > 0 ? 1 : 0, notifications[index].ReadRevision);
            Assert.Equal(index > 0 ? now : (DateTimeOffset?)null, notifications[index].ReadAt);
            Assert.Equal(index == 2 ? now : (DateTimeOffset?)null, notifications[index].DismissedAt);
        }
        // A schema rollback also preserves the reminder subset.
        await migrator.MigrateAsync("20261003214259_AddChatTaskCalendarImports", ct);
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM task_notifications").SingleAsync(ct));
        await migrator.MigrateAsync(cancellationToken: ct);
        Assert.Equal(3, await db.Notifications.CountAsync(ct));
    }
}
