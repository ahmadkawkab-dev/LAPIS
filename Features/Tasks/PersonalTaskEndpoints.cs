namespace Wukna.Features.Tasks;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wukna.Shared.Data.AppDbContext;
using Wukna.Features.Notifications;

public sealed record TaskWriteRequest(
    string? Title,
    string? Description,
    DateOnly? PlannedDate,
    TimeOnly? PlannedTime,
    string? TimeZoneId,
    Guid? ListId = null);

public sealed record PersonalTaskDto(
    Guid Id,
    string Title,
    string? Description,
    DateOnly? PlannedDate,
    TimeOnly? PlannedTime,
    string? TimeZoneId,
    DateTimeOffset? PlannedAtUtc,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? ListId)
{
    public static PersonalTaskDto From(PersonalTask task) => new(
        task.Id, task.Title, task.Description, task.PlannedDate, task.PlannedTime,
        task.TimeZoneId, task.PlannedAtUtc, task.CompletedAt, task.CreatedAt, task.UpdatedAt, task.ListId);
}

public sealed record PersonalTaskPageDto(IReadOnlyList<PersonalTaskDto> Items, bool HasMore);
public sealed record ClearTaskDayDto(int Deleted);
public sealed record TaskScheduleRequest(DateOnly? PlannedDate);

public static class PersonalTaskEndpoints
{
    public static IEndpointRouteBuilder MapPersonalTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tasks").RequireAuthorization();

        group.MapGet("/", async (string? view, string? date, int? limit, int? offset, Guid? listId,
            HttpContext context, WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            view ??= "inbox";
            if (view is not ("inbox" or "today" or "upcoming" or "all" or "completed" or "week"))
                return BadRequest("invalid_task_view");
            if (limit is < 1 or > 100 || offset is < 0 or > 10000)
                return BadRequest("invalid_task_page");
            if (view is "today" or "upcoming" or "week" && !DateOnly.TryParseExact(date,
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return BadRequest("invalid_task_date");
            if (listId is not null && !await db.PersonalTaskLists.AnyAsync(
                    list => list.Id == listId && list.UserId == userId, cancellationToken))
                return Results.NotFound();

            var query = db.PersonalTasks.AsNoTracking().Where(task => task.UserId == userId);
            if (listId is not null) query = query.Where(task => task.ListId == listId);
            if (view == "completed") query = query.Where(task => task.CompletedAt != null);
            else if (view != "week") query = query.Where(task => task.CompletedAt == null);
            if (view == "inbox") query = query.Where(task => task.PlannedDate == null);
            if (view == "week")
            {
                var firstDay = DateOnly.ParseExact(date!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (firstDay > DateOnly.MaxValue.AddDays(-7)) return BadRequest("invalid_task_date");
                var afterWeek = firstDay.AddDays(7);
                query = query.Where(task => task.PlannedDate >= firstDay && task.PlannedDate < afterWeek);
            }
            if (view is "today" or "upcoming")
            {
                var selectedDate = DateOnly.ParseExact(date!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (view == "today") query = query.Where(task => task.PlannedDate <= selectedDate);
                else query = query.Where(task => task.PlannedDate > selectedDate);
            }

            var pageSize = limit ?? 50;
            var tasks = await query
                .OrderBy(task => task.PlannedDate)
                .ThenBy(task => task.PlannedTime)
                .ThenBy(task => task.CreatedAt)
                .ThenBy(task => task.Id)
                .Skip(offset ?? 0).Take(pageSize + 1)
                .ToListAsync(cancellationToken);
            return Results.Ok(new PersonalTaskPageDto(
                tasks.Take(pageSize).Select(PersonalTaskDto.From).ToArray(),
                tasks.Count > pageSize));
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext context,
            WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var task = await db.PersonalTasks.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
            return task is null ? Results.NotFound() : Results.Ok(PersonalTaskDto.From(task));
        });

        group.MapPost("/", async (TaskWriteRequest request, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var error = Validate(request, out var plannedAtUtc);
            if (error is not null) return BadRequest(error);
            if (request.ListId is not null && !await db.PersonalTaskLists.AnyAsync(
                    list => list.Id == request.ListId && list.UserId == userId, cancellationToken))
                return BadRequest("invalid_task_list");
            var now = clock.GetUtcNow();
            var task = new PersonalTask
            {
                UserId = userId,
                ListId = request.ListId,
                Title = request.Title!.Trim(),
                Description = NormalizeDescription(request.Description),
                PlannedDate = request.PlannedDate,
                PlannedTime = request.PlannedTime,
                TimeZoneId = request.TimeZoneId,
                PlannedAtUtc = plannedAtUtc,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.PersonalTasks.Add(task);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException exception) when (IsMissingList(exception))
            { return BadRequest("invalid_task_list"); }
            return Results.Created($"/api/tasks/{task.Id}", PersonalTaskDto.From(task));
        });

        // PUT replaces the editable task fields, so null explicitly clears a schedule or description.
        group.MapPut("/{id:guid}", async (Guid id, TaskWriteRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var rows = await db.PersonalTasks.FromSqlInterpolated($"SELECT * FROM personal_tasks WHERE id = {id} AND user_id = {userId} FOR UPDATE").ToArrayAsync(cancellationToken);
            var task = rows.SingleOrDefault();
            if (task is null) return Results.NotFound();
            var error = Validate(request, out var plannedAtUtc);
            if (error is not null) return BadRequest(error);
            if (request.ListId is not null && !await db.PersonalTaskLists.AnyAsync(
                    list => list.Id == request.ListId && list.UserId == userId, cancellationToken))
                return BadRequest("invalid_task_list");
            task.Title = request.Title!.Trim();
            task.ListId = request.ListId;
            task.Description = NormalizeDescription(request.Description);
            task.PlannedDate = request.PlannedDate;
            task.PlannedTime = request.PlannedTime;
            task.TimeZoneId = request.TimeZoneId;
            task.PlannedAtUtc = plannedAtUtc;
            task.UpdatedAt = clock.GetUtcNow();
            await TaskReminderEndpoints.SyncForTask(db, task, task.UpdatedAt, cancellationToken);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException exception) when (IsMissingList(exception))
            { return BadRequest("invalid_task_list"); }
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(PersonalTaskDto.From(task));
        });

        group.MapPost("/{id:guid}/complete", async (Guid id, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
            await SetCompletion(id, context, db, clock, true, cancellationToken));
        group.MapPost("/{id:guid}/reopen", async (Guid id, HttpContext context,
            WuknaDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
            await SetCompletion(id, context, db, clock, false, cancellationToken));

        group.MapPost("/{id:guid}/schedule", async (Guid id, TaskScheduleRequest request,
            HttpContext context, WuknaDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var rows = await db.PersonalTasks.FromSqlInterpolated($"SELECT * FROM personal_tasks WHERE id = {id} AND user_id = {userId} FOR UPDATE").ToArrayAsync(cancellationToken);
            var task = rows.SingleOrDefault();
            if (task is null) return Results.NotFound();
            if (request.PlannedDate is null) return BadRequest("invalid_task_date");
            var candidate = new TaskWriteRequest(task.Title, task.Description, request.PlannedDate,
                task.PlannedTime, task.TimeZoneId, task.ListId);
            var error = Validate(candidate, out var plannedAtUtc);
            if (error is not null) return BadRequest(error);
            task.PlannedDate = request.PlannedDate;
            task.PlannedAtUtc = plannedAtUtc;
            task.UpdatedAt = clock.GetUtcNow();
            await TaskReminderEndpoints.SyncForTask(db, task, task.UpdatedAt, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(PersonalTaskDto.From(task));
        });

        group.MapDelete("/day/{date}", async (string date, HttpContext context,
            WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day)) return BadRequest("invalid_task_date");
            var deleted = await db.PersonalTasks
                .Where(task => task.UserId == userId && task.PlannedDate == day)
                .ExecuteDeleteAsync(cancellationToken);
            return Results.Ok(new ClearTaskDayDto(deleted));
        });

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context,
            WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var deleted = await db.PersonalTasks
                .Where(task => task.Id == id && task.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });
        return endpoints;
    }

    private static async Task<IResult> SetCompletion(Guid id, HttpContext context,
        WuknaDbContext db, TimeProvider clock, bool completed, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.PersonalTasks.FromSqlInterpolated($"SELECT * FROM personal_tasks WHERE id = {id} AND user_id = {userId} FOR UPDATE").ToArrayAsync(cancellationToken);
        var task = rows.SingleOrDefault();
        if (task is null) return Results.NotFound();
        if ((task.CompletedAt is not null) != completed)
        {
            task.CompletedAt = completed ? clock.GetUtcNow() : null;
            task.UpdatedAt = clock.GetUtcNow();
            await TaskReminderEndpoints.SyncForTask(db, task, task.UpdatedAt, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(PersonalTaskDto.From(task));
    }

    internal static string? Validate(TaskWriteRequest request, out DateTimeOffset? plannedAtUtc)
    {
        plannedAtUtc = null;
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            return "invalid_task_title";
        if (request.Description?.Length > 4000) return "invalid_task_description";
        if (request.PlannedTime is null)
            return request.TimeZoneId is null ? null : "invalid_task_schedule";
        if (request.PlannedDate is null || string.IsNullOrWhiteSpace(request.TimeZoneId) ||
            request.TimeZoneId.Length > 100) return "invalid_task_schedule";
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return "invalid_task_time_zone"; }
        catch (InvalidTimeZoneException) { return "invalid_task_time_zone"; }
        var local = request.PlannedDate.Value.ToDateTime(request.PlannedTime.Value, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            return "invalid_task_local_time";
        plannedAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        return null;
    }

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private static bool IsMissingList(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: "fk_personal_tasks_personal_task_lists_list_id" };

    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    private static IResult BadRequest(string code) => Results.BadRequest(new { code });
}
