namespace Wukna.Features.Tasks;

using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public sealed record TaskTemplateItem(string? Title, string? Description, TimeOnly? PlannedTime, string? TimeZoneId);
public sealed record TaskTemplateWriteRequest(string? Name, TaskTemplateItem[]? Items);
public sealed record TaskTemplateApplyRequest(DateOnly? PlannedDate);
public sealed record TaskTemplateDto(Guid Id, string Name, TaskTemplateItem[] Items, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TaskTemplateDto From(TaskTemplate template) => new(template.Id, template.Name,
        JsonSerializer.Deserialize<TaskTemplateItem[]>(template.ItemsJson) ?? [], template.CreatedAt, template.UpdatedAt);
}
public sealed record TaskTemplatePageDto(IReadOnlyList<TaskTemplateDto> Items, bool HasMore, int TotalCount);

public static class TaskTemplateEndpoints
{
    public static IEndpointRouteBuilder MapTaskTemplateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/task-templates").RequireAuthorization();
        group.MapGet("/page", async (string? search, int? offset, int? limit,
            HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (search?.Length > 80 || offset is < 0 or > 100000 || limit is < 1 or > 100)
                return BadRequest("invalid_task_template_page");
            var query = db.TaskTemplates.AsNoTracking().Where(item => item.UserId == userId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(item => item.Name.ToLower().Contains(term));
            }
            var totalCount = await query.CountAsync(ct);
            var size = limit ?? 50;
            var rows = await query.OrderBy(item => item.Name).ThenBy(item => item.Id)
                .Skip(offset ?? 0).Take(size + 1).ToListAsync(ct);
            return Results.Ok(new TaskTemplatePageDto(rows.Take(size).Select(TaskTemplateDto.From).ToArray(),
                rows.Count > size, totalCount));
        });
        group.MapGet("/{id:guid}", async (Guid id, HttpContext context, WuknaDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var template = await db.TaskTemplates.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == id && item.UserId == userId, ct);
            return template is null ? Results.NotFound() : Results.Ok(TaskTemplateDto.From(template));
        });
        group.MapGet("/", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var templates = await db.TaskTemplates.AsNoTracking().Where(item => item.UserId == userId)
                .OrderBy(item => item.Name).ThenBy(item => item.Id).ToListAsync(ct);
            return Results.Ok(templates.Select(TaskTemplateDto.From));
        });
        group.MapPost("/", async (TaskTemplateWriteRequest request, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var error = Validate(request);
            if (error is not null) return BadRequest(error);
            var now = clock.GetUtcNow();
            var template = new TaskTemplate
            {
                UserId = userId, Name = request.Name!.Trim(),
                ItemsJson = JsonSerializer.Serialize(Normalize(request.Items!)), CreatedAt = now, UpdatedAt = now
            };
            db.TaskTemplates.Add(template);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/task-templates/{template.Id}", TaskTemplateDto.From(template));
        });
        group.MapPut("/{id:guid}", async (Guid id, TaskTemplateWriteRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var template = await db.TaskTemplates.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, ct);
            if (template is null) return Results.NotFound();
            var error = Validate(request);
            if (error is not null) return BadRequest(error);
            template.Name = request.Name!.Trim();
            template.ItemsJson = JsonSerializer.Serialize(Normalize(request.Items!));
            template.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(TaskTemplateDto.From(template));
        });
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var deleted = await db.TaskTemplates.Where(item => item.Id == id && item.UserId == userId).ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });
        group.MapPost("/{id:guid}/apply", async (Guid id, TaskTemplateApplyRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var template = await db.TaskTemplates.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, ct);
            if (template is null) return Results.NotFound();
            if (request.PlannedDate is null) return BadRequest("invalid_task_date");
            var items = TaskTemplateDto.From(template).Items;
            var now = clock.GetUtcNow();
            var tasks = new List<PersonalTask>(items.Length);
            foreach (var item in items)
            {
                var write = new TaskWriteRequest(item.Title, item.Description, request.PlannedDate,
                    item.PlannedTime, item.TimeZoneId);
                var error = PersonalTaskEndpoints.Validate(write, out var plannedAtUtc);
                if (error is not null) return BadRequest(error);
                tasks.Add(new PersonalTask
                {
                    UserId = userId, Title = item.Title!.Trim(), Description = item.Description?.Trim(),
                    PlannedDate = request.PlannedDate, PlannedTime = item.PlannedTime,
                    TimeZoneId = item.TimeZoneId, PlannedAtUtc = plannedAtUtc,
                    CreatedAt = now, UpdatedAt = now
                });
            }
            db.PersonalTasks.AddRange(tasks);
            await db.SaveChangesAsync(ct);
            return Results.Ok(tasks.Select(PersonalTaskDto.From));
        });
        return endpoints;
    }

    private static TaskTemplateItem[] Normalize(TaskTemplateItem[] items) => items.Select(item =>
        new TaskTemplateItem(item.Title!.Trim(), string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
            item.PlannedTime, item.PlannedTime is null ? null : item.TimeZoneId)).ToArray();

    private static string? Validate(TaskTemplateWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80) return "invalid_task_template_name";
        if (request.Items is not { Length: > 0 and <= 50 }) return "invalid_task_template_items";
        foreach (var item in request.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Title) || item.Title.Trim().Length > 200 ||
                item.Description?.Length > 4000 || (item.PlannedTime is null) != (item.TimeZoneId is null) ||
                item.TimeZoneId?.Length > 100) return "invalid_task_template_item";
            if (item.TimeZoneId is not null)
            {
                try { _ = TimeZoneInfo.FindSystemTimeZoneById(item.TimeZoneId); }
                catch (TimeZoneNotFoundException) { return "invalid_task_time_zone"; }
                catch (InvalidTimeZoneException) { return "invalid_task_time_zone"; }
            }
        }
        return null;
    }

    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);
    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
}
