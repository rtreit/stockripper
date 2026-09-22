namespace Stockripper.Domain.Common;

internal static class UtcTimestamp
{
    public static DateTimeOffset Require(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must use the UTC offset.", parameterName);
        }

        var normalizedTicks = value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond);
        return new DateTimeOffset(normalizedTicks, TimeSpan.Zero);
    }
}
