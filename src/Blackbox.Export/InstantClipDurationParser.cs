using System.Globalization;

namespace Blackbox.Export;

public static class InstantClipDurationParser
{
    public static TimeSpan Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("Enter a clip duration.");
        }

        var normalized = value.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var timeSpan))
        {
            return Validate(timeSpan);
        }

        var multiplier = normalized.EndsWith("ms", StringComparison.Ordinal)
            ? TimeSpan.FromMilliseconds(1)
            : normalized.EndsWith('s')
                ? TimeSpan.FromSeconds(1)
                : normalized.EndsWith('m')
                    ? TimeSpan.FromMinutes(1)
                    : normalized.EndsWith('h')
                        ? TimeSpan.FromHours(1)
                        : TimeSpan.Zero;
        var suffixLength = normalized.EndsWith("ms", StringComparison.Ordinal) ? 2 : 1;
        if (multiplier == TimeSpan.Zero ||
            !double.TryParse(
                normalized[..^suffixLength],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            throw new FormatException("Use a duration such as 30s, 2m, or 00:02:00.");
        }

        return Validate(TimeSpan.FromTicks((long)(multiplier.Ticks * amount)));
    }

    private static TimeSpan Validate(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(30))
        {
            throw new FormatException("Clip duration must be between 1 second and 30 minutes.");
        }

        return duration;
    }
}
