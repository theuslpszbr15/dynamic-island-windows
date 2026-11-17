using DynamicIsland.Services;
using Xunit;

namespace DynamicIsland.Tests;

public class CalendarTests
{
    static readonly DateTime From = new(2026, 11, 16, 0, 0, 0), To = new(2026, 11, 18, 0, 0, 0);

    [Fact]
    public void ReadsAnEventWithItsTeamsLink()
    {
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20261116T140000\r\nDTEND:20261116T143000\r\nSUMMARY:Daily\\, time\r\n" +
                           "DESCRIPTION:Entrar: https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0?context=x\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var e = Assert.Single(CalendarService.ParseIcs(ics, From, To, "test"));
        Assert.Equal("Daily, time", e.Title);
        Assert.Equal(new DateTime(2026, 11, 16, 14, 0, 0), e.Start);
        Assert.StartsWith("https://teams.microsoft.com/l/meetup-join/", e.JoinUrl);
    }

    [Fact]
    public void UnfoldsLongLinesAndFindsMeet()
    {
        const string ics = "BEGIN:VEVENT\nDTSTART:20261117T090000\nSUMMARY:Planejamento\nLOCATION:https://meet.google.com/abc-defg-h\n ij\nEND:VEVENT\n";
        var e = Assert.Single(CalendarService.ParseIcs(ics, From, To, "test"));
        Assert.Equal("https://meet.google.com/abc-defg-hij", e.JoinUrl);
    }

    [Fact]
    public void ExpandsAWeeklyMeetingIntoThisWeek()
    {
        // every Monday and Tuesday since October, 10:00
        const string ics = "BEGIN:VEVENT\nDTSTART:20261005T100000\nDTEND:20261005T103000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU\nSUMMARY:Sync\n" +
                           "EXDATE:20261117T100000\nEND:VEVENT\n";
        var events = CalendarService.ParseIcs(ics, From, To, "test");
        var e = Assert.Single(events); // Tuesday the 17th was skipped
        Assert.Equal(new DateTime(2026, 11, 16, 10, 0, 0), e.Start);
    }

    [Fact]
    public void StopsARecurrenceAtUntil()
    {
        const string ics = "BEGIN:VEVENT\nDTSTART:20261101T080000\nRRULE:FREQ=DAILY;UNTIL=20261110T000000Z\nSUMMARY:Curso\nEND:VEVENT\n";
        Assert.Empty(CalendarService.ParseIcs(ics, From, To, "test"));
    }

    [Fact]
    public void SkipsAllDayAndCancelledEvents()
    {
        const string ics = "BEGIN:VEVENT\nDTSTART;VALUE=DATE:20261116\nSUMMARY:Feriado\nEND:VEVENT\n" +
                           "BEGIN:VEVENT\nDTSTART:20261116T150000\nSTATUS:CANCELLED\nSUMMARY:Cancelada\nEND:VEVENT\n";
        Assert.Empty(CalendarService.ParseIcs(ics, From, To, "test"));
    }

    [Theory]
    [InlineData("webcal://calendar.google.com/x/basic.ics", "https://calendar.google.com/x/basic.ics")]
    [InlineData("https://outlook.office365.com/owa/calendar/a/b/calendar.ics", "https://outlook.office365.com/owa/calendar/a/b/calendar.ics")]
    [InlineData("file:///C:/x.ics", null)]
    [InlineData("não é link", null)]
    public void NormalizesFeedLinks(string text, string? expected) => Assert.Equal(expected, CalendarService.NormalizeFeed(text));
}
