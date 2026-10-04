namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Auth;
using Wukna.Features.Tasks;
using Wukna.Features.Users;
using Xunit;

public sealed class PersonalTaskEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Scheduling_preserves_task_fields_and_local_time_across_daylight_saving()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("schedule-owner@wukna.test");
        var other = User("schedule-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);
        using var anonymous = factory.CreateClient();

        var unscheduled = await Create(client, new("Inbox item", "Keep notes", null, null, null), ct);
        using var planned = await client.PostAsJsonAsync($"/api/tasks/{unscheduled.Id}/schedule",
            new TaskScheduleRequest(new DateOnly(2026, 10, 1)), ct);
        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        var dateOnly = (await planned.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!;
        Assert.Equal(new DateOnly(2026, 10, 1), dateOnly.PlannedDate);
        Assert.Null(dateOnly.PlannedTime);
        Assert.Null(dateOnly.PlannedAtUtc);
        Assert.Equal("Keep notes", dateOnly.Description);

        using var createList = await client.PostAsJsonAsync("/api/task-lists", new TaskListWriteRequest("Project"), ct);
        var list = (await createList.Content.ReadFromJsonAsync<TaskListDto>(ct))!;
        var timed = await Create(client, new("Morning", "Keep this too", new DateOnly(2026, 3, 7),
            new TimeOnly(9, 0), "America/New_York", list.Id), ct);
        using var complete = await client.PostAsync($"/api/tasks/{timed.Id}/complete", null, ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var moved = await client.PostAsJsonAsync($"/api/tasks/{timed.Id}/schedule",
            new TaskScheduleRequest(new DateOnly(2026, 3, 9)), ct);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var result = (await moved.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!;
        Assert.Equal(new TimeOnly(9, 0), result.PlannedTime);
        Assert.Equal("America/New_York", result.TimeZoneId);
        Assert.Equal(list.Id, result.ListId);
        Assert.Equal("Morning", result.Title);
        Assert.Equal("Keep this too", result.Description);
        Assert.NotNull(result.CompletedAt);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 13, 0, 0, TimeSpan.Zero), result.PlannedAtUtc);

        using var denied = await otherClient.PostAsJsonAsync($"/api/tasks/{timed.Id}/schedule",
            new TaskScheduleRequest(new DateOnly(2026, 3, 10)), ct);
        using var unauthenticated = await anonymous.PostAsJsonAsync($"/api/tasks/{timed.Id}/schedule",
            new TaskScheduleRequest(new DateOnly(2026, 3, 10)), ct);
        using var invalid = await client.PostAsJsonAsync($"/api/tasks/{timed.Id}/schedule",
            new TaskScheduleRequest(null), ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var gapTask = await Create(client, new("Early", null, new DateOnly(2026, 3, 7),
            new TimeOnly(2, 30), "America/New_York"), ct);
        using var gap = await client.PostAsJsonAsync($"/api/tasks/{gapTask.Id}/schedule",
            new TaskScheduleRequest(new DateOnly(2026, 3, 8)), ct);
        Assert.Equal(HttpStatusCode.BadRequest, gap.StatusCode);
        var persisted = await client.GetFromJsonAsync<PersonalTaskDto>($"/api/tasks/{gapTask.Id}", ct);
        Assert.Equal(new DateOnly(2026, 3, 7), persisted!.PlannedDate);
        Assert.Equal(new TimeOnly(2, 30), persisted.PlannedTime);
    }

    [Fact]
    public async Task Lists_are_private_and_deleting_one_deletes_its_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("list-owner@wukna.test");
        var other = User("list-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);

        using var create = await client.PostAsJsonAsync("/api/task-lists", new TaskListWriteRequest(" Work "), ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var list = (await create.Content.ReadFromJsonAsync<TaskListDto>(ct))!;
        Assert.Equal("Work", list.Name);
        Assert.Single((await client.GetFromJsonAsync<TaskListDto[]>("/api/task-lists", ct))!);
        Assert.Empty((await otherClient.GetFromJsonAsync<TaskListDto[]>("/api/task-lists", ct))!);

        using var duplicate = await client.PostAsJsonAsync("/api/task-lists", new TaskListWriteRequest("work"), ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var stolenCreate = await otherClient.PostAsJsonAsync("/api/tasks",
            new TaskWriteRequest("Stolen", null, null, null, null, list.Id), ct);
        Assert.Equal(HttpStatusCode.BadRequest, stolenCreate.StatusCode);
        var task = await Create(client, new("Prepare slides", null, null, null, null, list.Id), ct);
        Assert.Equal(list.Id, task.ListId);
        var otherTask = await Create(otherClient, new("Other task", null, null, null, null), ct);
        using var stolenAssignment = await otherClient.PutAsJsonAsync($"/api/tasks/{otherTask.Id}",
            new TaskWriteRequest("Other task", null, null, null, null, list.Id), ct);
        Assert.Equal(HttpStatusCode.BadRequest, stolenAssignment.StatusCode);
        Assert.Equal(new[] { task.Id }, await Ids(client, $"all&listId={list.Id}", ct));
        using var stolenFilter = await otherClient.GetAsync($"/api/tasks?view=all&listId={list.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, stolenFilter.StatusCode);
        using var stolenRename = await otherClient.PutAsJsonAsync($"/api/task-lists/{list.Id}",
            new TaskListWriteRequest("Other"), ct);
        using var stolenDelete = await otherClient.DeleteAsync($"/api/task-lists/{list.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, stolenRename.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stolenDelete.StatusCode);

        using var rename = await client.PutAsJsonAsync($"/api/task-lists/{list.Id}",
            new TaskListWriteRequest("Projects"), ct);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal("Projects", (await rename.Content.ReadFromJsonAsync<TaskListDto>(ct))!.Name);
        using var delete = await client.DeleteAsync($"/api/task-lists/{list.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var deletedTask = await client.GetAsync($"/api/tasks/{task.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, deletedTask.StatusCode);
        Assert.DoesNotContain(task.Id, await Ids(client, "all", ct));
        await using var check = postgres.CreateContext();
        Assert.False(await check.PersonalTaskLists.AnyAsync(item => item.Id == list.Id, ct));
        Assert.False(await check.PersonalTasks.AnyAsync(item => item.Id == task.Id, ct));
        Assert.True(await check.PersonalTasks.AnyAsync(item => item.Id == otherTask.Id, ct));
    }

    [Fact]
    public async Task Planning_time_zone_is_saved_per_user_and_validated()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("zone-owner@wukna.test");
        var other = User("zone-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);
        using var anonymous = factory.CreateClient();

        Assert.Null((await client.GetFromJsonAsync<PlanningSettingsDto>("/api/tasks/settings", ct))!.TimeZoneId);
        using var invalid = await client.PutAsJsonAsync("/api/tasks/settings",
            new PlanningSettingsWriteRequest("Mars/Olympus"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var saved = await client.PutAsJsonAsync("/api/tasks/settings",
            new PlanningSettingsWriteRequest("Asia/Beirut"), ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Asia/Beirut", (await client.GetFromJsonAsync<PlanningSettingsDto>("/api/tasks/settings", ct))!.TimeZoneId);
        Assert.Null((await otherClient.GetFromJsonAsync<PlanningSettingsDto>("/api/tasks/settings", ct))!.TimeZoneId);
        using var anonymousRead = await anonymous.GetAsync("/api/tasks/settings", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
    }

    [Fact]
    public async Task Tasks_keep_unscheduled_date_only_and_timed_states_through_crud()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("task-owner@wukna.test");
        var other = User("task-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);

        var unscheduled = await Create(client, new("Buy groceries", null, null, null, null), ct);
        var dateOnly = await Create(client, new("Read docs", null, new DateOnly(2026, 9, 30), null, null), ct);
        var timed = await Create(client, new("Review PR", "Before lunch", new DateOnly(2026, 10, 1),
            new TimeOnly(9, 0), "Asia/Beirut"), ct);
        Assert.Null(unscheduled.PlannedDate);
        Assert.Null(dateOnly.PlannedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero), timed.PlannedAtUtc);

        Assert.Equal(new[] { unscheduled.Id }, await Ids(client, "inbox", ct));
        Assert.Equal(new[] { dateOnly.Id }, await Ids(client, "today&date=2026-09-30", ct));
        Assert.Equal(new[] { timed.Id }, await Ids(client, "upcoming&date=2026-09-30", ct));
        Assert.True((await client.GetFromJsonAsync<PersonalTaskPageDto>(
            "/api/tasks?view=all&limit=1", ct))!.HasMore);

        using var update = await client.PutAsJsonAsync($"/api/tasks/{unscheduled.Id}",
            new TaskWriteRequest("Buy groceries", "Milk", new DateOnly(2026, 9, 30), null, null), ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var changed = (await update.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!;
        Assert.Equal("Milk", changed.Description);
        Assert.Equal(new DateOnly(2026, 9, 30), changed.PlannedDate);
        Assert.Empty(await Ids(client, "inbox", ct));

        using var completed = await client.PostAsync($"/api/tasks/{changed.Id}/complete", null, ct);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(clock.GetUtcNow(), (await completed.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!.CompletedAt);
        Assert.Contains(changed.Id, await Ids(client, "completed", ct));
        using var reopened = await client.PostAsync($"/api/tasks/{changed.Id}/reopen", null, ct);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Null((await reopened.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!.CompletedAt);

        using var hidden = await otherClient.GetAsync($"/api/tasks/{changed.Id}", ct);
        using var deniedUpdate = await otherClient.PutAsJsonAsync($"/api/tasks/{changed.Id}",
            new TaskWriteRequest("Stolen", null, null, null, null), ct);
        using var deniedComplete = await otherClient.PostAsync($"/api/tasks/{changed.Id}/complete", null, ct);
        using var deniedDelete = await otherClient.DeleteAsync($"/api/tasks/{changed.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedComplete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedDelete.StatusCode);
        Assert.Empty(await Ids(otherClient, "all", ct));

        using var deleted = await client.DeleteAsync($"/api/tasks/{changed.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var check = postgres.CreateContext();
        Assert.False(await check.PersonalTasks.AnyAsync(task => task.Id == changed.Id, ct));
    }

    [Fact]
    public async Task Invalid_schedule_and_anonymous_requests_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var user = User("task-validation@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var anonymous = factory.CreateClient();
        using var client = Client(factory, user, clock);
        using var anonymousList = await anonymous.GetAsync("/api/tasks", ct);
        using var anonymousCreate = await anonymous.PostAsJsonAsync("/api/tasks",
            new TaskWriteRequest("No", null, null, null, null), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCreate.StatusCode);

        var invalid = new[]
        {
            new TaskWriteRequest(" ", null, null, null, null),
            new TaskWriteRequest("No date", null, null, new TimeOnly(9, 0), "Asia/Beirut"),
            new TaskWriteRequest("No zone", null, new DateOnly(2026, 9, 30), new TimeOnly(9, 0), null),
            new TaskWriteRequest("Gap", null, new DateOnly(2026, 3, 8), new TimeOnly(2, 30), "America/New_York"),
            new TaskWriteRequest("Overlap", null, new DateOnly(2026, 11, 1), new TimeOnly(1, 30), "America/New_York")
        };
        foreach (var request in invalid)
        {
            using var response = await client.PostAsJsonAsync("/api/tasks", request, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var invalidView = await client.GetAsync("/api/tasks?view=tomorrow", ct);
        using var invalidDate = await client.GetAsync("/api/tasks?view=today", ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalidView.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidDate.StatusCode);
    }

    [Fact]
    public async Task Week_reads_both_completion_states_and_templates_create_independent_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("template-owner@wukna.test");
        var other = User("template-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);

        var existing = await Create(client, new("Existing", null, new DateOnly(2026, 9, 28), null, null), ct);
        using var completed = await client.PostAsync($"/api/tasks/{existing.Id}/complete", null, ct);
        var outside = await Create(client, new("Outside", null, new DateOnly(2026, 10, 5), null, null), ct);
        var unscheduled = await Create(client, new("Quick", null, null, null, null), ct);

        var items = new[] { new TaskTemplateItem("Plan week", "Review goals", null, null),
            new TaskTemplateItem("Team meeting", null, new TimeOnly(10, 0), "Asia/Beirut") };
        using var created = await client.PostAsJsonAsync("/api/task-templates",
            new TaskTemplateWriteRequest("Monday routine", items), ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var template = (await created.Content.ReadFromJsonAsync<TaskTemplateDto>(ct))!;
        Assert.Equal(2, template.Items.Length);
        Assert.Empty((await otherClient.GetFromJsonAsync<TaskTemplateDto[]>("/api/task-templates", ct))!);
        using var stolen = await otherClient.PostAsJsonAsync($"/api/task-templates/{template.Id}/apply",
            new TaskTemplateApplyRequest(new DateOnly(2026, 10, 5)), ct);
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);

        using var applied = await client.PostAsJsonAsync($"/api/task-templates/{template.Id}/apply",
            new TaskTemplateApplyRequest(new DateOnly(2026, 9, 28)), ct);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var copies = (await applied.Content.ReadFromJsonAsync<PersonalTaskDto[]>(ct))!;
        Assert.Equal(2, copies.Length);
        Assert.All(copies, task => Assert.Null(task.CompletedAt));
        Assert.Equal(new TimeOnly(10, 0), copies[1].PlannedTime);
        var week = (await client.GetFromJsonAsync<PersonalTaskPageDto>(
            "/api/tasks?view=week&date=2026-09-28", ct))!;
        Assert.Equal(3, week.Items.Count);
        Assert.Contains(week.Items, task => task.Id == existing.Id && task.CompletedAt is not null);
        Assert.DoesNotContain(week.Items, task => task.Id == outside.Id || task.Id == unscheduled.Id);

        using var invalid = await client.PostAsJsonAsync("/api/task-templates",
            new TaskTemplateWriteRequest("Invalid", [new TaskTemplateItem("No date", null, new TimeOnly(9, 0), null)]), ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var gapTemplateResponse = await client.PostAsJsonAsync("/api/task-templates",
            new TaskTemplateWriteRequest("Gap", [new TaskTemplateItem("Early", null, new TimeOnly(2, 30), "America/New_York")]), ct);
        var gapTemplate = (await gapTemplateResponse.Content.ReadFromJsonAsync<TaskTemplateDto>(ct))!;
        var templatePage = (await client.GetFromJsonAsync<TaskTemplatePageDto>(
            "/api/task-templates/page?limit=1", ct))!;
        Assert.Equal(2, templatePage.TotalCount);
        Assert.Single(templatePage.Items);
        Assert.True(templatePage.HasMore);
        var nextTemplatePage = (await client.GetFromJsonAsync<TaskTemplatePageDto>(
            "/api/task-templates/page?limit=1&offset=1", ct))!;
        Assert.Single(nextTemplatePage.Items);
        Assert.False(nextTemplatePage.HasMore);
        Assert.NotEqual(templatePage.Items[0].Id, nextTemplatePage.Items[0].Id);
        Assert.Equal(template.Id, (await client.GetFromJsonAsync<TaskTemplateDto>(
            $"/api/task-templates/{template.Id}", ct))!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(
            $"/api/task-templates/{template.Id}", ct)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<TaskTemplatePageDto>(
            "/api/task-templates/page?search=monday", ct))!.Items);
        using var gapApply = await client.PostAsJsonAsync($"/api/task-templates/{gapTemplate.Id}/apply",
            new TaskTemplateApplyRequest(new DateOnly(2026, 3, 8)), ct);
        Assert.Equal(HttpStatusCode.BadRequest, gapApply.StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<PersonalTaskPageDto>(
            "/api/tasks?view=week&date=2026-03-02", ct))!.Items, task => task.Title == "Early");
        using var deletedTemplate = await client.DeleteAsync($"/api/task-templates/{template.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deletedTemplate.StatusCode);
        Assert.NotNull(await client.GetFromJsonAsync<PersonalTaskDto>($"/api/tasks/{copies[0].Id}", ct));
        Assert.NotNull(await client.GetFromJsonAsync<PersonalTaskDto>($"/api/tasks/{existing.Id}", ct));
    }

    [Fact]
    public async Task Clearing_a_day_removes_only_owners_tasks_on_that_day_including_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("clear-day-owner@wukna.test");
        var other = User("clear-day-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);
        using var anonymous = factory.CreateClient();
        var day = new DateOnly(2026, 10, 2);
        var active = await Create(client, new("Active", null, day, null, null), ct);
        var completed = await Create(client, new("Completed", null, day, null, null), ct);
        using (var response = await client.PostAsync($"/api/tasks/{completed.Id}/complete", null, ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tomorrow = await Create(client, new("Tomorrow", null, day.AddDays(1), null, null), ct);
        var otherTask = await Create(otherClient, new("Other user", null, day, null, null), ct);

        using var invalid = await client.DeleteAsync("/api/tasks/day/not-a-date", ct);
        using var denied = await anonymous.DeleteAsync("/api/tasks/day/2026-10-02", ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var cleared = await client.DeleteAsync("/api/tasks/day/2026-10-02", ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(2, (await cleared.Content.ReadFromJsonAsync<ClearTaskDayDto>(ct))!.Deleted);
        await using var check = postgres.CreateContext();
        Assert.False(await check.PersonalTasks.AnyAsync(task => task.Id == active.Id || task.Id == completed.Id, ct));
        Assert.True(await check.PersonalTasks.AnyAsync(task => task.Id == tomorrow.Id, ct));
        Assert.True(await check.PersonalTasks.AnyAsync(task => task.Id == otherTask.Id, ct));
    }

    private static async Task<PersonalTaskDto> Create(HttpClient client, TaskWriteRequest request, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/tasks", request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!;
    }

    private static async Task<Guid[]> Ids(HttpClient client, string view, CancellationToken ct)
    {
        var page = await client.GetFromJsonAsync<PersonalTaskPageDto>($"/api/tasks?view={view}", ct);
        return page!.Items.Select(task => task.Id).ToArray();
    }

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
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        EmailConfirmed = true
    };
}
