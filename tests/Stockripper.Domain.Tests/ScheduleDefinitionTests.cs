using System.Globalization;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Tests;

public sealed class ScheduleDefinitionTests
{
    private static readonly ScheduleDefinition Schedule = new(
        "America/New_York",
        new TimeOnly(9, 30),
        new TimeOnly(16, 0),
        [DayOfWeek.Monday, DayOfWeek.Tuesday]);

    [Theory]
    [InlineData("2026-08-18T13:29:59+00:00", false)]
    [InlineData("2026-08-18T13:30:00+00:00", true)]
    [InlineData("2026-08-18T19:59:59+00:00", true)]
    [InlineData("2026-08-18T20:00:00+00:00", false)]
    [InlineData("2026-08-19T16:00:00+00:00", false)]
    public void Schedule_uses_local_time_and_an_exclusive_end(string value, bool expected)
    {
        Assert.Equal(expected, Schedule.Allows(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture)));
    }
}
