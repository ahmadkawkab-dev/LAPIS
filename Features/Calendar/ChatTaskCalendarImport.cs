namespace Wukna.Features.Calendar;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wukna.Features.Chat;
using Wukna.Shared.Data.AppDbContext;

public static class ChatTaskCalendarImport
{
    public static Task<IResult> Get(Guid boardId, Guid messageId, HttpContext context,
        WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        Handle(boardId, messageId, context, db, clock, false, ct);

    public static Task<IResult> Add(Guid boardId, Guid messageId, HttpContext context,
        WuknaDbContext db, TimeProvider clock, CancellationToken ct) =>
        Handle(boardId, messageId, context, db, clock, true, ct);

    private static async Task<IResult> Handle(Guid boardId, Guid messageId, HttpContext context,
        WuknaDbContext db, TimeProvider clock, bool add, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        context.Response.Headers.CacheControl = "no-store";
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Hold current membership through creation, so removal cannot overtake authorization.
        if (await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null) return Results.NotFound();
        var task = await db.ScheduledChatTasks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.MessageId == messageId && item.Message.BoardId == boardId &&
            item.Message.Type == ChatMessageType.ScheduledTask, ct);
        if (task is null) return Results.NotFound();

        var created = false;
        if (add)
        {
            // Copy existing UTC instants instead of re-resolving ambiguous local times.
            // A start-only scheduled task remains start-only in Calendar.
            var zone = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
            var localStart = TimeZoneInfo.ConvertTime(task.StartsAtUtc, zone).DateTime;
            DateTime? localEnd = task.EndsAtUtc is { } end ? TimeZoneInfo.ConvertTime(end, zone).DateTime : null;
            // Raw SQL does not use CalendarEvent's property mapping. Bind authored
            // local times explicitly as timestamp without time zone, including null ends.
            var startParameter = new NpgsqlParameter("local_start", NpgsqlDbType.Timestamp) { Value = localStart };
            var endParameter = new NpgsqlParameter("local_end", NpgsqlDbType.Timestamp) { Value = (object?)localEnd ?? DBNull.Value };
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow();
            created = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calendar_events
                  (id, user_id, source_chat_message_id, title, description, is_all_day,
                   local_start, local_end, time_zone_id, start_at_utc, end_at_utc, created_at, updated_at)
                VALUES ({id}, {userId}, {messageId}, {task.Title}, {task.Description}, FALSE,
                  {startParameter}, {endParameter}, {task.TimeZoneId}, {task.StartsAtUtc}, {task.EndsAtUtc}, {now}, {now})
                ON CONFLICT (user_id, source_chat_message_id) WHERE source_chat_message_id IS NOT NULL
                DO NOTHING
                """, ct) == 1;
        }
        var item = await db.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.UserId == userId && candidate.SourceChatMessageId == messageId, ct);
        await transaction.CommitAsync(ct);
        if (item is null) return Results.NoContent();
        var dto = CalendarEventDto.From(item);
        return created ? Results.Created($"/api/calendar/events/{item.Id}", dto) : Results.Ok(dto);
    }
}
