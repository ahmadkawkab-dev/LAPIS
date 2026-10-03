namespace Wukna.Features.Chat;

using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using Wukna.Shared.Data.AppDbContext;

public interface ICalendarExportService
{
    byte[] Export(ChatMessage message);
}

public sealed class IcalNetCalendarExportService : ICalendarExportService
{
    public byte[] Export(ChatMessage message)
    {
        var task = message.ScheduledTask ?? throw new ArgumentException("A scheduled chat task is required.", nameof(message));
        var description = string.IsNullOrWhiteSpace(task.Description) ? string.Empty : task.Description + "\n\n";
        description += $"Scheduled in {task.TimeZoneId} (UTC{(task.OriginalOffsetMinutes < 0 ? "−" : "+")}" +
            $"{Math.Abs(task.OriginalOffsetMinutes) / 60:00}:{Math.Abs(task.OriginalOffsetMinutes) % 60:00}).";
        var calendarEvent = new CalendarEvent
        {
            Uid = $"wukna-chat-{message.Id:N}@wukna.invalid",
            DtStamp = new CalDateTime(message.CreatedAt.UtcDateTime),
            Start = new CalDateTime(task.StartsAtUtc.UtcDateTime),
            Summary = task.Title,
            Description = description,
        };
        if (task.EndsAtUtc is { } end) calendarEvent.End = new CalDateTime(end.UtcDateTime);
        var calendar = new Calendar();
        calendar.Events.Add(calendarEvent);
        return Encoding.UTF8.GetBytes(new CalendarSerializer().SerializeToString(calendar)
            ?? throw new InvalidOperationException("Calendar serialization returned no content."));
    }
}

public static class ChatCalendarExport
{
    public static async Task<IResult> Handle(Guid boardId, Guid messageId, HttpContext context,
        WuknaDbContext db, ICalendarExportService exporter, CancellationToken ct)
    {
        if (!ChatAccess.TryGetUserId(context, out var userId)) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null) return Results.NotFound();
        var message = await db.ChatMessages.AsNoTracking().Include(item => item.ScheduledTask)
            .SingleOrDefaultAsync(item => item.BoardId == boardId && item.Id == messageId &&
                item.Type == ChatMessageType.ScheduledTask, ct);
        if (message?.ScheduledTask is null) return Results.NotFound();
        var bytes = exporter.Export(message);
        await transaction.CommitAsync(ct);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(bytes, "text/calendar; charset=utf-8", $"wukna-task-{messageId:N}.ics");
    }
}
