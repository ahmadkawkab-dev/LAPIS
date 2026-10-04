namespace Wukna.Features.Tasks;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wukna.Shared.Data.AppDbContext;

public sealed record TaskListWriteRequest(string? Name);
public sealed record TaskListDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TaskListDto From(PersonalTaskList list) =>
        new(list.Id, list.Name, list.CreatedAt, list.UpdatedAt);
}
public sealed record PlanningSettingsDto(string? TimeZoneId);
public sealed record PlanningSettingsWriteRequest(string? TimeZoneId);

public static class TaskPlanningEndpoints
{
    public static IEndpointRouteBuilder MapTaskPlanningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var lists = endpoints.MapGroup("/api/task-lists").RequireAuthorization();
        lists.MapGet("/", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var items = await db.PersonalTaskLists.AsNoTracking()
                .Where(list => list.UserId == userId).OrderBy(list => list.Name).ThenBy(list => list.Id)
                .Select(list => new TaskListDto(list.Id, list.Name, list.CreatedAt, list.UpdatedAt))
                .ToListAsync(ct);
            return Results.Ok(items);
        });
        lists.MapPost("/", async (TaskListWriteRequest request, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!ValidName(request.Name)) return BadRequest("invalid_task_list_name");
            var name = request.Name!.Trim();
            var normalizedName = name.ToUpperInvariant();
            if (await db.PersonalTaskLists.AnyAsync(list => list.UserId == userId &&
                    list.NormalizedName == normalizedName, ct)) return Conflict("task_list_name_taken");
            var now = clock.GetUtcNow();
            var list = new PersonalTaskList
            {
                UserId = userId, Name = name, NormalizedName = normalizedName,
                CreatedAt = now, UpdatedAt = now
            };
            db.PersonalTaskLists.Add(list);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException exception) when (IsNameConflict(exception))
            { return Conflict("task_list_name_taken"); }
            return Results.Created($"/api/task-lists/{list.Id}", TaskListDto.From(list));
        });
        lists.MapPut("/{id:guid}", async (Guid id, TaskListWriteRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var list = await db.PersonalTaskLists.SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.UserId == userId, ct);
            if (list is null) return Results.NotFound();
            if (!ValidName(request.Name)) return BadRequest("invalid_task_list_name");
            var name = request.Name!.Trim();
            var normalizedName = name.ToUpperInvariant();
            if (await db.PersonalTaskLists.AnyAsync(candidate => candidate.UserId == userId &&
                    candidate.Id != id && candidate.NormalizedName == normalizedName, ct))
                return Conflict("task_list_name_taken");
            list.Name = name;
            list.NormalizedName = normalizedName;
            list.UpdatedAt = clock.GetUtcNow();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException exception) when (IsNameConflict(exception))
            { return Conflict("task_list_name_taken"); }
            return Results.Ok(TaskListDto.From(list));
        });
        lists.MapDelete("/{id:guid}", async (Guid id, HttpContext context,
            WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var deleted = await db.PersonalTaskLists.Where(list => list.Id == id && list.UserId == userId)
                .ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        var settings = endpoints.MapGroup("/api/tasks/settings").RequireAuthorization();
        settings.MapGet("/", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var zone = await db.PlanningSettings.AsNoTracking()
                .Where(item => item.UserId == userId).Select(item => item.TimeZoneId)
                .SingleOrDefaultAsync(ct);
            return Results.Ok(new PlanningSettingsDto(zone));
        });
        settings.MapPut("/", async (PlanningSettingsWriteRequest request, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var zone = request.TimeZoneId?.Trim();
            if (string.IsNullOrWhiteSpace(zone) || zone.Length > 100 || !ValidTimeZone(zone))
                return BadRequest("invalid_task_time_zone");
            var item = await db.PlanningSettings.SingleOrDefaultAsync(
                candidate => candidate.UserId == userId, ct);
            if (item is null)
            {
                item = new PlanningSettings { UserId = userId, TimeZoneId = zone, UpdatedAt = clock.GetUtcNow() };
                db.PlanningSettings.Add(item);
            }
            else
            {
                item.TimeZoneId = zone;
                item.UpdatedAt = clock.GetUtcNow();
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PlanningSettingsDto(item.TimeZoneId));
        });
        return endpoints;
    }

    private static bool ValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 80;

    private static bool ValidTimeZone(string zone)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static bool IsNameConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_personal_task_lists_user_name" };

    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
    private static IResult Conflict(string code) => Results.Conflict(new { code });
}
