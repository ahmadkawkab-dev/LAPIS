namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Auth;
using Wukna.Features.Calendar;
using Wukna.Features.Tasks;
using Wukna.Features.Users;
using Xunit;

public sealed class CalendarEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Calendar_range_can_load_items_beyond_first_category_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("calendar-overflow@wukna.test");
        var other = User("calendar-overflow-other@wukna.test");
        var now = DateTimeOffset.UtcNow;
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            for (var i = 0; i < 501; i++) db.PersonalTasks.Add(new PersonalTask
            {
                UserId = owner.Id, Title = $"Item {i}", PlannedDate = new DateOnly(2026, 10, 2),
                CreatedAt = now, UpdatedAt = now
            });
            db.PersonalTasks.Add(new PersonalTask { UserId = other.Id, Title = "Private",
                PlannedDate = new DateOnly(2026, 10, 2), CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(now);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(
            "/api/calendar?from=2026-10-01&to=2026-10-08&timeZone=UTC", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
            "/api/calendar?from=2026-10-01&to=2026-10-08&timeZone=UTC&offset=1", ct)).StatusCode);
        var first = (await client.GetFromJsonAsync<CalendarRangeDto>(
            "/api/calendar?from=2026-10-01&to=2026-10-08&timeZone=UTC", ct))!;
        Assert.Equal(500, first.Items.Count);
        Assert.True(first.HasMore);
        var second = (await client.GetFromJsonAsync<CalendarRangeDto>(
            "/api/calendar?from=2026-10-01&to=2026-10-08&timeZone=UTC&offset=500", ct))!;
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
    }

    [Fact]
    public async Task Events_support_all_day_and_timed_crud_with_owner_isolation()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("calendar-owner@wukna.test");
        var other = User("calendar-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);

        var allDay = await Create(client, new("Birthday", null, null, true,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), null, null, null), ct);
        Assert.Null(allDay.StartAtUtc);
        Assert.Equal(new DateOnly(2026, 10, 2), allDay.AllDayEndDateExclusive);
        var timed = await Create(client, new("Dentist", "Checkup", "Clinic", false,
            null, null, Local(2026, 10, 1, 15), Local(2026, 10, 1, 16), "Asia/Beirut"), ct);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), timed.StartAtUtc);
        Assert.Equal("Clinic", timed.Location);
        Assert.Equal(timed.Id, (await client.GetFromJsonAsync<CalendarEventDto>(
            $"/api/calendar/events/{timed.Id}", ct))!.Id);

        using var hidden = await otherClient.GetAsync($"/api/calendar/events/{timed.Id}", ct);
        using var deniedUpdate = await otherClient.PutAsJsonAsync($"/api/calendar/events/{timed.Id}",
            new CalendarEventWriteRequest("Changed", null, null, true,
                new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), null, null, null), ct);
        using var deniedDelete = await otherClient.DeleteAsync($"/api/calendar/events/{timed.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedDelete.StatusCode);

        using var update = await client.PutAsJsonAsync($"/api/calendar/events/{timed.Id}",
            new CalendarEventWriteRequest("Dentist moved", null, null, true,
                new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3), null, null, null), ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var changed = (await update.Content.ReadFromJsonAsync<CalendarEventDto>(ct))!;
        Assert.True(changed.IsAllDay);
        Assert.Null(changed.TimeZoneId);
        Assert.Null(changed.StartAtUtc);
        Assert.Equal(new DateOnly(2026, 10, 2), changed.AllDayStartDate);

        using var deleted = await client.DeleteAsync($"/api/calendar/events/{allDay.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var check = postgres.CreateContext();
        Assert.False(await check.CalendarEvents.AnyAsync(item => item.Id == allDay.Id, ct));
        Assert.True(await check.CalendarEvents.AnyAsync(item => item.Id == timed.Id, ct));
    }

    [Fact]
    public async Task Calendar_range_includes_overlapping_events_and_scheduled_tasks_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        var owner = User("range-owner@wukna.test");
        var other = User("range-other@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, other);
            await db.SaveChangesAsync(ct);
        }
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);
        using var otherClient = Client(factory, other, clock);

        var spanning = await Create(client, new("Trip", null, null, true,
            new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 3), null, null, null), ct);
        var crossing = await Create(client, new("Late call", null, null, false,
            null, null, Local(2026, 9, 30, 23), Local(2026, 10, 1, 1), "UTC"), ct);
        var outside = await Create(client, new("Later", null, null, true,
            new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 4), null, null, null), ct);
        var dateTask = await CreateTask(client, new("Anytime", null,
            new DateOnly(2026, 10, 1), null, null), ct);
        var timedTask = await CreateTask(client, new("Morning", null,
            new DateOnly(2026, 10, 1), new TimeOnly(9, 0), "Asia/Beirut"), ct);
        var unscheduled = await CreateTask(client, new("Inbox", null, null, null, null), ct);
        var hidden = await Create(otherClient, new("Private", null, null, true,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), null, null, null), ct);

        var page = (await client.GetFromJsonAsync<CalendarRangeDto>(
            "/api/calendar?from=2026-10-01&to=2026-10-02&timeZone=Asia%2FBeirut", ct))!;
        var ids = page.Items.Select(item => item.Id).ToArray();
        Assert.Contains(spanning.Id, ids);
        Assert.Contains(crossing.Id, ids);
        Assert.Contains(dateTask.Id, ids);
        Assert.Contains(timedTask.Id, ids);
        Assert.DoesNotContain(outside.Id, ids);
        Assert.DoesNotContain(unscheduled.Id, ids);
        Assert.DoesNotContain(hidden.Id, ids);
        Assert.False(page.HasMore);
        Assert.Contains(page.Items, item => item.Id == dateTask.Id && item.ScheduleKind == "dateOnlyTask");
        Assert.Contains(page.Items, item => item.Id == crossing.Id && item.ScheduleKind == "timedEvent");

        foreach (var query in new[] {
            "from=2026-10-01&to=2026-10-01&timeZone=UTC",
            "from=2026-10-01&to=2026-12-01&timeZone=UTC",
            "from=2026-10-01&to=2026-10-02&timeZone=Mars%2FOlympus"
        })
        {
            using var invalid = await client.GetAsync($"/api/calendar?{query}", ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var gap = await client.PostAsJsonAsync("/api/calendar/events",
            new CalendarEventWriteRequest("Gap", null, null, false, null, null,
                Local(2026, 3, 8, 2, 30), Local(2026, 3, 8, 3, 30), "America/New_York"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, gap.StatusCode);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync("/api/calendar?from=2026-10-01&to=2026-10-02&timeZone=UTC", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    private static DateTime Local(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    private static async Task<CalendarEventDto> Create(HttpClient client, CalendarEventWriteRequest request, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/calendar/events", request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CalendarEventDto>(ct))!;
    }

    private static async Task<PersonalTaskDto> CreateTask(HttpClient client, TaskWriteRequest request, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/tasks", request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PersonalTaskDto>(ct))!;
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
