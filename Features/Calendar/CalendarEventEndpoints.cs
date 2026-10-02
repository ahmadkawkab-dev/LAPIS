namespace Wukna.Features.Calendar;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed record CalendarEventWriteRequest(
    string? Title,
    string? Description,
    string? Location,
    bool IsAllDay,
    DateOnly? AllDayStartDate,
    DateOnly? AllDayEndDateExclusive,
    DateTime? LocalStart,
    DateTime? LocalEnd,
    string? TimeZoneId);

public sealed record CalendarEventDto(
    Guid Id,
    string Title,
    string? Description,
    string? Location,
    bool IsAllDay,
    DateOnly? AllDayStartDate,
    DateOnly? AllDayEndDateExclusive,
    DateTime? LocalStart,
    DateTime? LocalEnd,
    string? TimeZoneId,
    DateTimeOffset? StartAtUtc,
    DateTimeOffset? EndAtUtc,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CalendarEventDto From(CalendarEvent item) => new(
        item.Id, item.Title, item.Description, item.Location, item.IsAllDay,
        item.AllDayStartDate, item.AllDayEndDateExclusive, item.LocalStart, item.LocalEnd,
        item.TimeZoneId, item.StartAtUtc, item.EndAtUtc, item.CreatedAt, item.UpdatedAt);
}

public static class CalendarEventEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/calendar/events").RequireAuthorization();
        group.MapGet("/{id:guid}", async (Guid id, HttpContext context,
            WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var item = await db.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.UserId == userId, ct);
            return item is null ? Results.NotFound() : Results.Ok(CalendarEventDto.From(item));
        });
        group.MapPost("/", async (CalendarEventWriteRequest request, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var error = Validate(request, out var startAtUtc, out var endAtUtc);
            if (error is not null) return BadRequest(error);
            var now = clock.GetUtcNow();
            var item = new CalendarEvent { UserId = userId, CreatedAt = now, UpdatedAt = now };
            Assign(item, request, startAtUtc, endAtUtc);
            db.CalendarEvents.Add(item);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/calendar/events/{item.Id}", CalendarEventDto.From(item));
        });
        group.MapPut("/{id:guid}", async (Guid id, CalendarEventWriteRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var item = await db.CalendarEvents.SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.UserId == userId, ct);
            if (item is null) return Results.NotFound();
            var error = Validate(request, out var startAtUtc, out var endAtUtc);
            if (error is not null) return BadRequest(error);
            Assign(item, request, startAtUtc, endAtUtc);
            item.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(CalendarEventDto.From(item));
        });
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context,
            WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var deleted = await db.CalendarEvents.Where(item => item.Id == id && item.UserId == userId)
                .ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });
        return endpoints;
    }

    private static string? Validate(CalendarEventWriteRequest request,
        out DateTimeOffset? startAtUtc, out DateTimeOffset? endAtUtc)
    {
        startAtUtc = null;
        endAtUtc = null;
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            return "invalid_calendar_event_title";
        if (request.Description?.Length > 4000 || request.Location?.Length > 200)
            return "invalid_calendar_event_details";
        if (request.IsAllDay)
        {
            if (request.AllDayStartDate is null || request.AllDayEndDateExclusive is null ||
                request.AllDayEndDateExclusive <= request.AllDayStartDate ||
                request.LocalStart is not null || request.LocalEnd is not null || request.TimeZoneId is not null)
                return "invalid_calendar_event_schedule";
            return null;
        }
        if (request.AllDayStartDate is not null || request.AllDayEndDateExclusive is not null ||
            request.LocalStart is null || request.LocalEnd is null ||
            request.LocalStart.Value.Kind != DateTimeKind.Unspecified ||
            request.LocalEnd.Value.Kind != DateTimeKind.Unspecified ||
            request.LocalEnd <= request.LocalStart ||
            string.IsNullOrWhiteSpace(request.TimeZoneId) || request.TimeZoneId.Length > 100)
            return "invalid_calendar_event_schedule";
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return "invalid_calendar_event_time_zone"; }
        catch (InvalidTimeZoneException) { return "invalid_calendar_event_time_zone"; }
        if (zone.IsInvalidTime(request.LocalStart.Value) || zone.IsAmbiguousTime(request.LocalStart.Value) ||
            zone.IsInvalidTime(request.LocalEnd.Value) || zone.IsAmbiguousTime(request.LocalEnd.Value))
            return "invalid_calendar_event_local_time";
        startAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(request.LocalStart.Value, zone));
        endAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(request.LocalEnd.Value, zone));
        return endAtUtc <= startAtUtc ? "invalid_calendar_event_schedule" : null;
    }

    private static void Assign(CalendarEvent item, CalendarEventWriteRequest request,
        DateTimeOffset? startAtUtc, DateTimeOffset? endAtUtc)
    {
        item.Title = request.Title!.Trim();
        item.Description = Normalize(request.Description);
        item.Location = Normalize(request.Location);
        item.IsAllDay = request.IsAllDay;
        item.AllDayStartDate = request.AllDayStartDate;
        item.AllDayEndDateExclusive = request.AllDayEndDateExclusive;
        item.LocalStart = request.LocalStart;
        item.LocalEnd = request.LocalEnd;
        item.TimeZoneId = request.TimeZoneId;
        item.StartAtUtc = startAtUtc;
        item.EndAtUtc = endAtUtc;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
}
