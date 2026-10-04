namespace Wukna.Features.Calendar;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed record CalendarItemDto(
    string Source,
    Guid Id,
    string Title,
    string ScheduleKind,
    DateOnly? StartDate,
    DateOnly? EndDateExclusive,
    DateTimeOffset? StartAtUtc,
    DateTimeOffset? EndAtUtc,
    string? TimeZoneId,
    bool? IsCompleted);

public sealed record CalendarRangeDto(IReadOnlyList<CalendarItemDto> Items, bool HasMore);

public static class CalendarRangeEndpoints
{
    private const int CategoryLimit = 500;

    public static IEndpointRouteBuilder MapCalendarRangeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/calendar", async (string? from, string? to, string? timeZone, int? offset,
            HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId))
                return Results.Unauthorized();
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var startDate) ||
                !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var endDate) ||
                endDate.DayNumber - startDate.DayNumber is < 1 or > 42)
                return BadRequest("invalid_calendar_range");
            if (string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 100)
                return BadRequest("invalid_calendar_time_zone");
            if (offset.HasValue && (offset is < 0 or > 100000 || offset.Value % CategoryLimit != 0))
                return BadRequest("invalid_calendar_page");
            var skip = offset ?? 0;
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
            catch (TimeZoneNotFoundException) { return BadRequest("invalid_calendar_time_zone"); }
            catch (InvalidTimeZoneException) { return BadRequest("invalid_calendar_time_zone"); }
            var startUtc = StartOfDayUtc(startDate, zone);
            var endUtc = StartOfDayUtc(endDate, zone);

            var dateTasks = await db.PersonalTasks.AsNoTracking()
                .Where(task => task.UserId == userId && task.PlannedDate >= startDate &&
                    task.PlannedDate < endDate && task.PlannedTime == null)
                .OrderBy(task => task.PlannedDate).ThenBy(task => task.Id)
                .Skip(skip).Take(CategoryLimit + 1).ToListAsync(ct);
            var timedTasks = await db.PersonalTasks.AsNoTracking()
                .Where(task => task.UserId == userId && task.PlannedAtUtc >= startUtc &&
                    task.PlannedAtUtc < endUtc)
                .OrderBy(task => task.PlannedAtUtc).ThenBy(task => task.Id)
                .Skip(skip).Take(CategoryLimit + 1).ToListAsync(ct);
            var allDayEvents = await db.CalendarEvents.AsNoTracking()
                .Where(item => item.UserId == userId && item.IsAllDay &&
                    item.AllDayStartDate < endDate && item.AllDayEndDateExclusive > startDate)
                .OrderBy(item => item.AllDayStartDate).ThenBy(item => item.Id)
                .Skip(skip).Take(CategoryLimit + 1).ToListAsync(ct);
            var timedEvents = await db.CalendarEvents.AsNoTracking()
                .Where(item => item.UserId == userId && !item.IsAllDay &&
                    item.StartAtUtc < endUtc && (item.EndAtUtc > startUtc ||
                        item.EndAtUtc == null && item.StartAtUtc >= startUtc))
                .OrderBy(item => item.StartAtUtc).ThenBy(item => item.Id)
                .Skip(skip).Take(CategoryLimit + 1).ToListAsync(ct);

            var items = dateTasks.Take(CategoryLimit).Select(task => new CalendarItemDto(
                    "task", task.Id, task.Title, "dateOnlyTask", task.PlannedDate, null,
                    null, null, null, task.CompletedAt is not null))
                .Concat(timedTasks.Take(CategoryLimit).Select(task => new CalendarItemDto(
                    "task", task.Id, task.Title, "timedTask", null, null,
                    task.PlannedAtUtc, null, task.TimeZoneId, task.CompletedAt is not null)))
                .Concat(allDayEvents.Take(CategoryLimit).Select(item => new CalendarItemDto(
                    "event", item.Id, item.Title, "allDayEvent", item.AllDayStartDate,
                    item.AllDayEndDateExclusive, null, null, null, null)))
                .Concat(timedEvents.Take(CategoryLimit).Select(item => new CalendarItemDto(
                    "event", item.Id, item.Title, "timedEvent", null, null,
                    item.StartAtUtc, item.EndAtUtc, item.TimeZoneId, null)))
                .OrderBy(item => item.StartDate ?? DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(item.StartAtUtc!.Value, zone).DateTime))
                .ThenBy(item => item.ScheduleKind is "allDayEvent" or "dateOnlyTask" ? 0 : 1)
                .ThenBy(item => item.StartAtUtc)
                .ThenBy(item => item.Id)
                .ToArray();
            return Results.Ok(new CalendarRangeDto(items,
                dateTasks.Count > CategoryLimit || timedTasks.Count > CategoryLimit ||
                allDayEvents.Count > CategoryLimit || timedEvents.Count > CategoryLimit));
        }).RequireAuthorization();
        return endpoints;
    }

    private static DateTimeOffset StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
}
