using System.Collections.Immutable;
using Stockripper.Domain.Common;

namespace Stockripper.Domain.Goals;

public sealed record ScheduleDefinition
{
    public ScheduleDefinition(
        string timeZoneId,
        TimeOnly startTime,
        TimeOnly endTime,
        IEnumerable<DayOfWeek> activeDays)
    {
        ArgumentNullException.ThrowIfNull(activeDays);

        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId != timeZoneId.Trim())
        {
            throw new DomainRuleViolationException("The schedule time zone is required.");
        }

        if (startTime >= endTime)
        {
            throw new DomainRuleViolationException("The schedule start must be before the schedule end.");
        }

        TimeZoneId = timeZoneId;
        StartTime = startTime;
        EndTime = endTime;
        ActiveDays = activeDays.Distinct().Order().ToImmutableArray();

        if (ActiveDays.IsEmpty)
        {
            throw new DomainRuleViolationException("The schedule needs at least one active day.");
        }
    }

    public string TimeZoneId { get; }

    public TimeOnly StartTime { get; }

    public TimeOnly EndTime { get; }

    public ImmutableArray<DayOfWeek> ActiveDays { get; }

    public bool Allows(DateTimeOffset at)
    {
        var checkedAt = UtcTimestamp.Require(at, nameof(at));
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new DomainRuleViolationException("The schedule time zone is unavailable.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new DomainRuleViolationException("The schedule time zone is invalid.");
        }

        var local = TimeZoneInfo.ConvertTime(checkedAt, timeZone);
        var localTime = TimeOnly.FromDateTime(local.DateTime);
        return ActiveDays.Contains(local.DayOfWeek)
            && localTime >= StartTime
            && localTime < EndTime;
    }
}
