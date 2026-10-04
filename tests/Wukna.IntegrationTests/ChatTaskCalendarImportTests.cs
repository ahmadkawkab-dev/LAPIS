namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Board;
using Wukna.Features.Calendar;
using Wukna.Features.Chat;
using Xunit;
using static ChatControlTestSupport;

public sealed class ChatTaskCalendarImportTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Import_is_atomic_per_user_and_preserves_existing_task_details()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var guest = Client(factory, seed.Guest);
        var request = new CreateScheduledChatTaskRequest(Guid.NewGuid(), "Deploy beta", "Release notes",
            Local(2026, 10, 9, 14), Local(2026, 10, 9, 15, 30), "Asia/Beirut", null, null);
        var message = await Schedule(owner, seed, request, ct);
        var path = ImportPath(seed, message.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.GetAsync(path, ct)).StatusCode);

        // Concurrent clicks/tabs and a client-supplied identity cannot change ownership or content.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => guest.PostAsJsonAsync(path,
            new { userId = seed.Owner.Id, title = "Tampered" }, ct)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            var items = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<CalendarEventDto>(ct)));
            var item = Assert.IsType<CalendarEventDto>(items[0]);
            Assert.All(items, candidate => Assert.Equal(item.Id, candidate!.Id));
            Assert.Equal(message.Id, item.SourceChatMessageId);
            Assert.Equal(request.Title, item.Title);
            Assert.Equal(request.Description, item.Description);
            Assert.Equal(request.LocalStart, item.LocalStart);
            Assert.Equal(request.LocalEnd, item.LocalEnd);
            Assert.Equal(request.TimeZoneId, item.TimeZoneId);
            Assert.Equal(message.ScheduledTask!.StartsAtUtc, item.StartAtUtc);
            Assert.Equal(message.ScheduledTask.EndsAtUtc, item.EndAtUtc);
            Assert.Equal(item.Id, (await guest.GetFromJsonAsync<CalendarEventDto>(path, ct))!.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/calendar/events/{item.Id}", ct)).StatusCode);
            using var replay = await guest.PostAsync(path, null, ct);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(item.Id, (await replay.Content.ReadFromJsonAsync<CalendarEventDto>(ct))!.Id);
        }
        finally { foreach (var response in responses) response.Dispose(); }

        using var own = await owner.PostAsync(path, null, ct);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.CalendarEvents.CountAsync(ct));
        Assert.Equal(1, await db.CalendarEvents.CountAsync(item => item.UserId == seed.Guest.Id, ct));
        Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        Assert.Equal(1, await db.ScheduledChatTasks.CountAsync(ct));
        Assert.Equal(1, await db.ChatOutboxEvents.CountAsync(ct));
        Assert.Equal(0, await db.PersonalTasks.CountAsync(ct));
        Assert.Equal(0, await db.TaskReminders.CountAsync(ct));
    }

    [Fact]
    public async Task Import_and_status_require_current_board_access_and_a_scheduled_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var guest = Client(factory, seed.Guest);
        using var outsider = Client(factory, seed.Outsider);
        using var anonymous = factory.CreateClient();
        var message = await Schedule(owner, seed, new(Guid.NewGuid(), "Meeting", null,
            Local(2026, 10, 9, 14), null, "UTC", null, null), ct);
        var path = ImportPath(seed, message.Id);
        using var otherCreated = await owner.PostAsJsonAsync("/api/boards", new CreateBoardRequest("Other board"), ct);
        Assert.Equal(HttpStatusCode.Created, otherCreated.StatusCode);
        var otherBoard = (await otherCreated.Content.ReadFromJsonAsync<BoardDetailDto>(ct))!;
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var denied = await anonymous.SendAsync(new(method, path), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var hidden = await outsider.SendAsync(new(method, path), ct);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            using var missing = await owner.SendAsync(new(method, ImportPath(seed, Guid.NewGuid())), ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var wrongBoard = await owner.SendAsync(new(method,
                $"/api/calendar/events/from-chat/{Guid.NewGuid()}/{message.Id}"), ct);
            Assert.Equal(HttpStatusCode.NotFound, wrongBoard.StatusCode);
            using var mismatched = await owner.SendAsync(new(method,
                $"/api/calendar/events/from-chat/{otherBoard.Id}/{message.Id}"), ct);
            Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
        }
        using var textResponse = await Send(owner, seed, ct);
        var text = (await textResponse.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message;
        using var wrongType = await owner.PostAsync(ImportPath(seed, text.Id), null, ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongType.StatusCode);
        using var added = await guest.PostAsync(path, null, ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        using var removed = await Remove(owner, seed, seed.Guest, ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var denied = await guest.SendAsync(new(method, path), ct);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        await using var db = postgres.CreateContext();
        Assert.Single(await db.CalendarEvents.ToListAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_preserves_optional_end_and_DST_instants_and_survives_source_expiry(bool withEnd)
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var guest = Client(factory, seed.Guest);
        var message = await Schedule(owner, seed, new(Guid.NewGuid(), "DST meeting", "Keep this",
            Local(2026, 11, 1, 1, 30), withEnd ? Local(2026, 11, 1, 1, 15) : null,
            "America/New_York", withEnd ? -240 : -300, withEnd ? -300 : null), ct);
        using var response = await guest.PostAsync(ImportPath(seed, message.Id), null, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<CalendarEventDto>(ct))!;
        Assert.Equal(message.ScheduledTask!.StartsAtUtc, item.StartAtUtc);
        Assert.Equal(message.ScheduledTask.EndsAtUtc, item.EndAtUtc);
        Assert.Equal(Local(2026, 11, 1, 1, 30), item.LocalStart);
        Assert.Equal(withEnd ? Local(2026, 11, 1, 1, 15) : null, item.LocalEnd);
        var range = (await guest.GetFromJsonAsync<CalendarRangeDto>(
            "/api/calendar?from=2026-11-01&to=2026-11-02&timeZone=UTC", ct))!;
        Assert.Equal(item.Id, Assert.Single(range.Items).Id);
        var outside = (await guest.GetFromJsonAsync<CalendarRangeDto>(
            "/api/calendar?from=2026-11-02&to=2026-11-03&timeZone=UTC", ct))!;
        Assert.Empty(outside.Items);

        // Simulate expiry/deletion of the source. Calendar is the user's durable snapshot.
        await using (var db = postgres.CreateContext())
            await db.ChatMessages.Where(candidate => candidate.Id == message.Id).ExecuteDeleteAsync(ct);
        var retained = (await guest.GetFromJsonAsync<CalendarEventDto>($"/api/calendar/events/{item.Id}", ct))!;
        Assert.Equal(item, retained);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync(ImportPath(seed, message.Id), null, ct)).StatusCode);
    }

    private static DateTime Local(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
    private static string ImportPath(SeedData seed, Guid messageId) =>
        $"/api/calendar/events/from-chat/{seed.Board.Id}/{messageId}";
    private static async Task<ChatMessageDto> Schedule(HttpClient http, SeedData seed,
        CreateScheduledChatTaskRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Path(seed) + "/scheduled-tasks", request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChatSendResultDto>(ct))!.Message;
    }
}
