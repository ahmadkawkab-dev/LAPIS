namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Auth;
using Wukna.Features.Notifications;
using Wukna.Features.Tasks;
using Wukna.Features.Users;
using Xunit;

public sealed class TaskReminderEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Notification_pages_report_full_counts_and_keep_users_isolated()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("notification-owner@wukna.test");
        var other = User("notification-other@wukna.test");
        var now = DateTimeOffset.UtcNow;
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            for (var i = 0; i < 105; i++)
            {
                var task = Task(owner.Id, $"Task {i}", now);
                db.PersonalTasks.Add(task);
                db.Notifications.Add(new Notification { Id = task.Id, TaskId = task.Id, UserId = owner.Id,
                    IssuedAt = now.AddMinutes(-i), ReadAt = i < 5 ? now : null, ReadRevision = i < 5 ? 1 : 0 });
            }
            var foreign = Task(other.Id, "Private", now);
            db.PersonalTasks.Add(foreign);
            db.Notifications.Add(new Notification { Id = foreign.Id, TaskId = foreign.Id, UserId = other.Id,
                IssuedAt = now });
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(now);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/notifications/page", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/notifications/page?cursor=bad", ct)).StatusCode);

        var first = (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page?limit=30", ct))!;
        Assert.Equal(105, first.TotalCount);
        Assert.Equal(100, first.UnreadCount);
        Assert.Equal(30, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = (await client.GetFromJsonAsync<NotificationPageDto>(
            $"/api/notifications/page?limit=30&cursor={first.NextCursor}", ct))!;
        Assert.Equal(30, second.Items.Count);
        Assert.Empty(first.Items.Select(item => item.TaskId).Intersect(second.Items.Select(item => item.TaskId)));
        var unread = (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page?limit=10&unreadOnly=true", ct))!;
        Assert.Equal(10, unread.Items.Count);
        Assert.All(unread.Items, item => Assert.Null(item.ReadAt));
        Assert.Equal(1, (await otherClient.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page", ct))!.TotalCount);

        using var read = await client.PostAsync($"/api/notifications/{unread.Items[0].TaskId}/read", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        using var readAll = await client.PostAsync("/api/notifications/read-all", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, readAll.StatusCode);
        var afterRead = (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page", ct))!;
        Assert.Equal(0, afterRead.UnreadCount);
        using var dismiss = await client.PostAsync($"/api/notifications/{first.Items[0].TaskId}/dismiss", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);
        Assert.Equal(104, (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page", ct))!.TotalCount);
    }

    [Fact]
    public async Task Reminder_processes_once_and_snooze_rearms_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("reminder-owner@wukna.test");
        var other = User("reminder-other@wukna.test");
        var now = DateTimeOffset.UtcNow;
        var planned = now.AddHours(2);
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(now);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);
        using var created = await client.PostAsJsonAsync("/api/tasks",
            new TaskWriteRequest("Call", null, DateOnly.FromDateTime(planned.UtcDateTime),
                new TimeOnly(planned.Hour, planned.Minute), "UTC"), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = (await created.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!;
        using var denied = await otherClient.PutAsJsonAsync($"/api/tasks/{task.Id}/reminder",
            new ReminderWriteRequest(30), ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var put = await client.PutAsJsonAsync($"/api/tasks/{task.Id}/reminder",
            new ReminderWriteRequest(30), ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var due = (await put.Content.ReadFromJsonAsync<TaskReminderDto>(ct))!.DueAtUtc;
        Assert.Equal(0, await Process(due.AddMinutes(-1), ct));
        Assert.Equal(1, await Process(due, ct));
        Assert.Equal(0, await Process(due, ct));
        Assert.Equal(1, (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page", ct))!.UnreadCount);

        clock.Advance(due - now);
        using var snooze = await client.PostAsJsonAsync($"/api/notifications/{task.Id}/snooze",
            new SnoozeRequest(10), ct);
        Assert.Equal(HttpStatusCode.NoContent, snooze.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page", ct))!.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<UpcomingReminderPageDto>(
            "/api/notifications/upcoming/page", ct))!.TotalCount);
        Assert.Equal(0, await Process(due.AddMinutes(9), ct));
        Assert.Equal(1, await Process(due.AddMinutes(10), ct));
        Assert.Equal(0, await Process(due.AddMinutes(10), ct));
        Assert.Equal(1, (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page", ct))!.UnreadCount);

        using var moved = await client.PutAsJsonAsync($"/api/tasks/{task.Id}",
            new TaskWriteRequest(task.Title, null, task.PlannedDate!.Value.AddDays(1),
                task.PlannedTime, "UTC"), ct);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<NotificationPageDto>(
            "/api/notifications/page", ct))!.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<UpcomingReminderPageDto>(
            "/api/notifications/upcoming/page", ct))!.TotalCount);
        using var cancelled = await client.DeleteAsync($"/api/tasks/{task.Id}/reminder", ct);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<UpcomingReminderPageDto>(
            "/api/notifications/upcoming/page", ct))!.TotalCount);
        using var restored = await client.PutAsJsonAsync($"/api/tasks/{task.Id}/reminder",
            new ReminderWriteRequest(30), ct);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var completed = await client.PostAsync($"/api/tasks/{task.Id}/complete", null, ct);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.GetAsync($"/api/tasks/{task.Id}/reminder", ct)).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<UpcomingReminderPageDto>(
            "/api/notifications/upcoming/page", ct))!.TotalCount);

        async Task<int> Process(DateTimeOffset at, CancellationToken token)
        {
            await using var db = postgres.CreateContext();
            return await TaskReminderWorker.ProcessDue(db, at, token);
        }
    }

    [Fact]
    public async Task Upcoming_pages_include_all_scheduled_reminders_without_foreign_records()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("upcoming-owner@wukna.test");
        var other = User("upcoming-other@wukna.test");
        var now = DateTimeOffset.UtcNow;
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            for (var i = 0; i < 55; i++)
            {
                var task = Task(owner.Id, $"Upcoming {i}", now);
                db.PersonalTasks.Add(task);
                db.TaskReminders.Add(new TaskReminder { TaskId = task.Id, UserId = owner.Id,
                    DueAtUtc = now.AddHours(1).AddMinutes(i), CreatedAt = now, UpdatedAt = now });
            }
            var foreign = Task(other.Id, "Private", now);
            db.PersonalTasks.Add(foreign);
            db.TaskReminders.Add(new TaskReminder { TaskId = foreign.Id, UserId = other.Id,
                DueAtUtc = now.AddHours(1), CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(now);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);
        var first = (await client.GetFromJsonAsync<UpcomingReminderPageDto>(
            "/api/notifications/upcoming/page?limit=30", ct))!;
        Assert.Equal(55, first.TotalCount);
        Assert.Equal(30, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = (await client.GetFromJsonAsync<UpcomingReminderPageDto>(
            $"/api/notifications/upcoming/page?limit=30&cursor={first.NextCursor}", ct))!;
        Assert.Equal(25, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Items.Select(item => item.TaskId).Intersect(second.Items.Select(item => item.TaskId)));
        Assert.Equal(1, (await otherClient.GetFromJsonAsync<UpcomingReminderPageDto>(
            "/api/notifications/upcoming/page", ct))!.TotalCount);
    }

    private static PersonalTask Task(Guid userId, string title, DateTimeOffset now) => new()
    {
        UserId = userId, Title = title, CreatedAt = now, UpdatedAt = now
    };
    private static HttpClient Client(WuknaWebApplicationFactory factory, User user, TimeProvider clock)
    {
        var client = factory.CreateClient();
        var token = new JwtTokenGenerator(new JwtOptions
        {
            Issuer = WuknaWebApplicationFactory.JwtIssuer,
            Audience = WuknaWebApplicationFactory.JwtAudience,
            SigningKey = WuknaWebApplicationFactory.JwtSigningKey,
            AccessTokenMinutes = 60,
            RefreshTokenDays = 7
        }, clock).CreateAccessToken(user).Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private static User User(string email) => new()
    {
        Email = email, NormalizedEmail = email.ToUpperInvariant(), UserName = email,
        NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true
    };
}
