namespace Wukna.IntegrationTests;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wukna.Features.Chat;
using Wukna.Features.Calendar;
using Wukna.Features.Notifications;
using Wukna.Features.Notes;
using Xunit;
using static ChatControlTestSupport;

public sealed class NotificationDeliveryTests(PostgresFixture postgres)
{
    private static Task DispatchNotifications(Factory factory, CancellationToken ct) => factory.Services.GetRequiredService<NotificationDispatcher>().ProcessAsync(ct);
    private static async Task<ChatMessageDto> Message(HttpClient http, SeedData seed, SendChatMessageRequest request, CancellationToken ct)
    {
        using var result = await http.PostAsJsonAsync(Path(seed) + "/messages", request, ct);
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync(ct));
        return (await result.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message;
    }
    [Fact]
    public async Task Mentions_and_replies_are_validated_idempotent_and_filtered_by_current_preferences()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner);
        using var guest = Client(factory, seed.Guest); using var peer = Client(factory, seed.Peer); using var outsider = Client(factory, seed.Outsider);
        var settingsPath = $"/api/boards/{seed.Board.Id}/notification-preferences";
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync(settingsPath, new BoardNotificationPreferenceWriteRequest("mentionsAndReplies", false, null, 0), ct)).StatusCode);
        var ordinary = await Message(owner, seed, new(Guid.NewGuid(), "ordinary"), ct);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); await DispatchNotifications(factory, ct);
        Assert.Empty((await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!);
        Assert.Single((await peer.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!);
        var body = "Hello @" + seed.Guest.Username;
        var mention = new ChatMentionDto(seed.Guest.Id, 6, body.Length - 6);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Path(seed) + "/messages", new SendChatMessageRequest(Guid.NewGuid(), body, [mention with { UserId = seed.Outsider.Id }]), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Path(seed) + "/messages", new { clientMessageId = Guid.NewGuid(), body, mentions = new object?[] { null } }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(Path(seed) + "/mention-members", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(Path(seed) + "/mention-members", ct)).StatusCode);
        var request = new SendChatMessageRequest(Guid.NewGuid(), body, [mention], ordinary.Id);
        var posted = await Message(owner, seed, request, ct); var replay = await Message(owner, seed, request, ct);
        Assert.Equal(posted.Id, replay.Id); Assert.Equal(ordinary.Id, posted.ReplyToMessageId); Assert.True(posted.NotifyReplyAuthor);
        seed.Clock.Advance(TimeSpan.FromSeconds(3));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => DispatchNotifications(factory, ct)));
        await DispatchNotifications(factory, ct);
        var notifications = (await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!;
        Assert.Equal("mention", Assert.Single(notifications).ActivityKind);
        Assert.Empty((await owner.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!);
        var parent = await Message(guest, seed, new(Guid.NewGuid(), "Parent"), ct);
        await Message(owner, seed, new(Guid.NewGuid(), "Reply without ping", null, parent.Id, false), ct);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); await DispatchNotifications(factory, ct);
        Assert.Single((await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!);
        await Message(owner, seed, new(Guid.NewGuid(), "Reply with ping", null, parent.Id), ct);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); await DispatchNotifications(factory, ct);
        Assert.Contains((await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!, item => item.ActivityKind == "reply");
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync(settingsPath, new BoardNotificationPreferenceWriteRequest("muted", false, null, 1), ct)).StatusCode);
        await Message(owner, seed, new(Guid.NewGuid(), body, [mention]), ct);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); await DispatchNotifications(factory, ct);
        Assert.Equal(2, (await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!.Length);
    }

    [Fact]
    public async Task Visible_read_chat_stays_quiet_and_removed_members_cannot_receive_old_incarnations()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        var state = await State(guest, seed, ct); var message = await Message(owner, seed, new(Guid.NewGuid(), "seen"), ct);
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync(Path(seed) + "/read", new SetChatReadRequest(message.Cursor, state.MembershipInstanceId), ct)).StatusCode);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); await DispatchNotifications(factory, ct);
        Assert.Empty((await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!);
        await Message(owner, seed, new(Guid.NewGuid(), "queued before removal"), ct);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, seed, seed.Guest, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Invite(owner, seed, seed.Guest, ct)).StatusCode);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); await DispatchNotifications(factory, ct);
        var items = (await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!;
        Assert.DoesNotContain(items, item => item.Type == "chatActivity");
        Assert.Single(items, item => item.Type == "boardInvitation");
        var first = await Message(owner, seed, new(Guid.NewGuid(), "one"), ct);
        var second = await Message(owner, seed, new(Guid.NewGuid(), "two"), ct);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); await DispatchNotifications(factory, ct);
        var activity = Assert.Single((await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!, item => item.Type == "chatActivity");
        Assert.Equal(2, activity.ActivityCount); Assert.Equal(2, activity.Revision);
        Assert.Equal(second.Id, activity.ResourceId);
        var newState = await State(guest, seed, ct);
        await guest.PutAsJsonAsync(Path(seed) + "/read", new SetChatReadRequest(first.Cursor, newState.MembershipInstanceId), ct);
        Assert.True((await guest.GetFromJsonAsync<NotificationDto>($"/api/notifications/{activity.Id}", ct))!.IsUnread);
        await guest.PutAsJsonAsync(Path(seed) + "/read", new SetChatReadRequest(second.Cursor, newState.MembershipInstanceId), ct);
        Assert.False((await guest.GetFromJsonAsync<NotificationDto>($"/api/notifications/{activity.Id}", ct))!.IsUnread);
    }

    [Fact]
    public async Task Imported_calendar_reminders_survive_chat_expiry_and_rescheduling_invalidates_queued_alerts()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        var start = seed.Clock.GetUtcNow().AddDays(3).UtcDateTime;
        using var shared = await owner.PostAsJsonAsync(Path(seed) + "/scheduled-tasks", new CreateScheduledChatTaskRequest(Guid.NewGuid(), "Workshop", "Details",
            DateTime.SpecifyKind(start, DateTimeKind.Unspecified), DateTime.SpecifyKind(start.AddHours(1), DateTimeKind.Unspecified), "UTC", null, null), ct);
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        var message = (await shared.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message;
        using var imported = await guest.PostAsync($"/api/calendar/events/from-chat/{seed.Board.Id}/{message.Id}", null, ct);
        var item = (await imported.Content.ReadFromJsonAsync<CalendarEventDto>(ct))!;
        var reminderPath = $"/api/calendar/events/{item.Id}/reminder";
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync(reminderPath, new ReminderWriteRequest(10), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PutAsJsonAsync(reminderPath, new ReminderWriteRequest(10), ct)).StatusCode);
        var upcoming = (await guest.GetFromJsonAsync<UpcomingReminderPageDto>("/api/notifications/upcoming/page", ct))!;
        Assert.Contains(upcoming.Items, reminder => reminder.TaskId == item.Id && reminder.ResourceKind == "calendarEvent");
        seed.Clock.Advance(TimeSpan.FromDays(3));
        // Removal and expired source do not erase the owner's personal calendar snapshot/reminder.
        await Remove(owner, seed, seed.Guest, ct);
        await using (var db = postgres.CreateContext()) {
            Assert.Equal(1, await CalendarReminderEndpoints.ProcessDue(db, seed.Clock.GetUtcNow(), ct));
            Assert.Equal(0, await CalendarReminderEndpoints.ProcessDue(db, seed.Clock.GetUtcNow(), ct));
        }
        var due = Assert.Single((await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!, entry => entry.Type == "scheduledTaskReminder");
        Assert.Equal(item.Id, due.ResourceId); Assert.Equal("Workshop", due.Title);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync(reminderPath + "/snooze", new SnoozeRequest(10), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/notifications/{due.Id}", ct)).StatusCode);
        await DispatchNotifications(factory, ct);
        seed.Clock.Advance(TimeSpan.FromMinutes(11));
        await using (var db = postgres.CreateContext()) Assert.Equal(1, await CalendarReminderEndpoints.ProcessDue(db, seed.Clock.GetUtcNow(), ct));
        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync(reminderPath, ct)).StatusCode);
        Assert.DoesNotContain((await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!, entry => entry.Type == "scheduledTaskReminder");
        await using var final = postgres.CreateContext(); Assert.Single(await final.CalendarEvents.ToArrayAsync(ct)); Assert.Empty(await final.PersonalTasks.ToArrayAsync(ct));
    }

    [Fact]
    public async Task Board_semantic_changes_notify_other_members_but_geometry_and_no_op_updates_do_not()
    {
        var ct = TestContext.Current.CancellationToken; var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        var path = $"/api/boards/{seed.Board.Id}/notes";
        using var created = await owner.PostAsJsonAsync(path, new CreateNoteRequest(NoteKind.Standalone, "Task", "", null, 0, 0, null, null, null, null, false), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var note = (await created.Content.ReadFromJsonAsync<NoteDto>(ct))!;
        async Task<NoteDto> Patch(object patch) {
            using var request = new HttpRequestMessage(HttpMethod.Patch, path + $"/{note.Id}") { Content = JsonContent.Create(patch) };
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{note.Version}\""); using var response = await owner.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<NoteDto>(ct))!;
        }
        await using (var db = postgres.CreateContext()) Assert.Equal(2, await db.NotificationWork.CountAsync(item => item.Kind == NotificationWorkKind.Generate, ct));
        note = await Patch(new { positionX = 10, width = 300 }); note = await Patch(new { title = "Task" });
        await using (var db = postgres.CreateContext()) Assert.Equal(2, await db.NotificationWork.CountAsync(item => item.Kind == NotificationWorkKind.Generate, ct));
        note = await Patch(new { isCompleted = true }); note = await Patch(new { isCompleted = true });
        await DispatchNotifications(factory, ct);
        var items = (await guest.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!;
        Assert.Single(items, item => item.ActivityKind == "taskCompleted");
        Assert.Single(items, item => item.Type == "sharedBoardActivity");
        Assert.Empty((await owner.GetFromJsonAsync<NotificationDto[]>("/api/notifications", ct))!);
    }
}
