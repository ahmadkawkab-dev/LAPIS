namespace Wukna.IntegrationTests;

using System.Text;
using Ical.Net;
using Wukna.Features.Chat;
using Xunit;

public sealed class ChatCalendarExportTests
{
    [Fact]
    public void Export_is_stable_UTC_and_escapes_untrusted_calendar_text()
    {
        var id = Guid.Parse("d4225159-17ed-433c-9570-7e021e6d1e2b");
        var message = new ChatMessage
        {
            Id = id, Type = ChatMessageType.ScheduledTask,
            CreatedAt = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero),
            ScheduledTask = new ScheduledChatTask
            {
                Title = "Deploy, beta; build\r\nBEGIN:VEVENT\r\nUID:forged",
                Description = "Release notes\r\nATTENDEE:mailto:forged@example.com",
                StartsAtUtc = new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
                EndsAtUtc = new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.Zero),
                TimeZoneId = "Asia/Beirut", OriginalOffsetMinutes = 180,
            }
        };
        var exporter = new IcalNetCalendarExportService();
        var bytes = exporter.Export(message);
        Assert.Equal(bytes, exporter.Export(message));
        var content = Encoding.UTF8.GetString(bytes);
        var calendar = Assert.IsType<Calendar>(Calendar.Load(content));
        var entry = Assert.Single(calendar.Events);
        Assert.Equal(message.ScheduledTask!.Title.Replace("\r\n", "\n"),
            Assert.IsType<string>(entry.Summary).Replace("\r\n", "\n"));
        Assert.Contains("Release notes", entry.Description);
        Assert.Contains("Scheduled in Asia/Beirut", entry.Description);
        Assert.Equal($"wukna-chat-{id:N}@wukna.invalid", entry.Uid);
        Assert.Contains("DTSTAMP:20261003T093000Z", content);
        Assert.Contains("DTSTART:20261009T110000Z", content);
        Assert.Contains("DTEND:20261009T123000Z", content);
        Assert.Single(content.Split('\n'), line => line.TrimEnd('\r') == "BEGIN:VEVENT");
        Assert.DoesNotContain("\r\nATTENDEE:mailto:forged@example.com", content);
        Assert.DoesNotContain("\r\nUID:forged", content);
    }

    [Fact]
    public void Export_without_end_does_not_invent_an_end_instant()
    {
        var message = new ChatMessage { ScheduledTask = new ScheduledChatTask {
            Title = "Start", StartsAtUtc = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            TimeZoneId = "America/New_York", OriginalOffsetMinutes = -240 } };
        var content = Encoding.UTF8.GetString(new IcalNetCalendarExportService().Export(message));
        Assert.Contains("DTSTART:20261101T053000Z", content);
        Assert.DoesNotContain("DTEND:", content);
    }
}
