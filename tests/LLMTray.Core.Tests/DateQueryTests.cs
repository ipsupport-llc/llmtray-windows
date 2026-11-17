using System.Globalization;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Tests;

/// <summary>The Mac's DateQueryTests, ported.</summary>
public class DateQueryTests
{
    static readonly DateQuery.Day Today = new(2026, 11, 16);

    static string? Iso(string text, bool monthFirst = false) => DateQuery.Parse(text, Today, monthFirst)?.Iso;

    [Theory]
    [InlineData("2024-04-10")]
    [InlineData("2024-04-10T12:30:00Z")]
    [InlineData("2024/04/10")]
    [InlineData("2024.4.10")]
    [InlineData("Apr 10, 2024")]
    [InlineData("April 10, 2024")]
    [InlineData("Apr. 10th, 2024")]
    [InlineData("10 April 2024")]
    [InlineData("Wednesday, April 10, 2024")]
    [InlineData("10.04.2024")]
    [InlineData("10-04-2024")]
    [InlineData("10.04.24")]
    [InlineData("10 апреля 2024")]
    [InlineData("10 апреля 2024 г.")]
    [InlineData("10-го апреля 2024")]
    [InlineData("10 квітня 2024")]
    [InlineData("10. April 2024")]
    [InlineData("10 avril 2024")]
    [InlineData("apr 10 2024")]
    [InlineData("10 de abril de 2024")]
    [InlineData("le 10 avril 2024")]
    [InlineData("mar. 10 avril 2024")]
    [InlineData("Apr 10, 24")]
    [InlineData("10-Apr-24")]
    [InlineData("2024 Apr 10")]
    [InlineData("2024-04-10 12:30")]
    [InlineData("10.04.2024.")]
    public void Forms(string text) => Assert.Equal("2024-04-10", Iso(text));

    [Fact]
    public void OtherDays()
    {
        Assert.Equal("2025-09-03", Iso("Sept 3, 2025"));
        Assert.Equal("1999-04-10", Iso("Apr 10, 99"));  // two digits: at most 20 years ahead
        Assert.Equal("2100-04-10", DateQuery.Parse("Apr 10, 00", new DateQuery.Day(2080, 1, 1), false)?.Iso);
        Assert.Equal("2024-03-05", Iso("mar. 5 mar. 2024"));  // the same month twice
        Assert.Equal("2026-03-05", Iso("March 5"));  // no year: this year
        Assert.Equal("2026-11-17", Iso("tomorrow"));
        Assert.Equal("2026-11-15", Iso("вчера"));
        Assert.Equal("2026-11-16", Iso("Today"));
        Assert.Equal("2024-02-29", Iso("2024-02-29"));
    }

    [Fact]
    public void SlashOrder()
    {
        Assert.Equal("2024-04-10", Iso("04/10/2024", monthFirst: true));
        Assert.Equal("2024-10-04", Iso("04/10/2024", monthFirst: false));
        Assert.True(DateQuery.Parse("04/10/2024", Today, true)!.Ambiguous);
        Assert.Equal("2024-04-13", Iso("13/04/2024", monthFirst: true));  // a number over 12 is the day
        Assert.Equal("2024-04-13", Iso("04/13/2024", monthFirst: false));
        Assert.False(DateQuery.Parse("04/13/2024", Today, false)!.Ambiguous);
        Assert.Equal("2024-04-10", Iso("04-10-2024", monthFirst: true));  // a dash fits both orders too
        Assert.Equal("2024-10-04", Iso("04.10.2024", monthFirst: true));  // dots are day first
        Assert.Equal("2024-04-10", Iso("Wed. 04/10/2024", monthFirst: true));  // a weekday's dot isn't a separator
        Assert.Equal("2024-04-05", Iso("04/05/2024.", monthFirst: true));  // a full stop after it isn't a dotted date
    }

    [Theory]
    [InlineData("")]
    [InlineData("next friday")]
    [InlineData("2024-02-30")]
    [InlineData("31.04.2024")]
    [InlineData("Apr 2024 10 11")]
    [InlineData("banana 10 2024")]
    [InlineData("10/04")]
    [InlineData("April May 3 2024")]
    [InlineData("2024-04-100")]
    [InlineData("2024-04-10xyz")]
    [InlineData("2024-04-10 garbage")]
    [InlineData("2024-04-10-05")]
    [InlineData("٢٠٢٤-٠٤-١٠")]
    [InlineData("2024-٠٤-10")]
    [InlineData("Apr 10, 999")]
    [InlineData("April 2024")]
    public void Rejects(string text) => Assert.Null(Iso(text));

    [Fact]
    public void Answers()
    {
        var o = DateQuery.Describe(new DateQuery.Day(2024, 4, 10), Today);
        Assert.Equal("2024-04-10", o["date"]!.GetValue<string>());
        Assert.Equal("Wednesday", o["weekday"]!.GetValue<string>());
        Assert.Equal("2026-11-16", o["today"]!.GetValue<string>());
        Assert.Equal(-950, o["days_from_today"]!.GetValue<int>());
        Assert.False(o.ContainsKey("note"));
        // Proleptic Gregorian: no ten days lost in 1582.
        Assert.Equal(10, DateQuery.Describe(new DateQuery.Day(1582, 10, 20), new DateQuery.Day(1582, 10, 10))["days_from_today"]!.GetValue<int>());
        Assert.Equal("Monday", DateQuery.Describe(new DateQuery.Day(1, 1, 1), Today)["weekday"]!.GetValue<string>());
        Assert.Equal("2028-02-29", DateQuery.Parse("tomorrow", new DateQuery.Day(2028, 2, 28), false)?.Iso);
        Assert.Equal("2027-01-01", DateQuery.Parse("tomorrow", new DateQuery.Day(2026, 12, 31), false)?.Iso);
        var ambiguous = DateQuery.Describe(new DateQuery.Day(2024, 4, 10, Ambiguous: true), Today);
        Assert.Equal("read as April 10; the other order would be October 4", ambiguous["note"]!.GetValue<string>());
    }

    [Fact]
    public void TodayAndOrderComeFromTheUser()
    {
        var now = new DateTimeOffset(2026, 11, 16, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal("2026-11-16", DateQuery.Today(now, TimeZoneInfo.Utc).Iso);
        Assert.Equal("2026-11-17", DateQuery.Today(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).Iso);
        Assert.True(DateQuery.IsMonthFirst(CultureInfo.GetCultureInfo("en-US")));
        Assert.False(DateQuery.IsMonthFirst(CultureInfo.GetCultureInfo("ru-RU")));
        Assert.False(DateQuery.IsMonthFirst(CultureInfo.GetCultureInfo("en-GB")));
    }
}
