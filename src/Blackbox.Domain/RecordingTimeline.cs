namespace Blackbox.Domain;

public static class RecordingTimeline
{
    public static DateTimeOffset ToTimestamp(RecordingSession session, TimeSpan offset)
    {
        var remaining = offset < TimeSpan.Zero ? TimeSpan.Zero : offset;
        foreach (var segment in session.Segments.OrderBy(static segment => segment.StartTime))
        {
            var duration = segment.EndTime - segment.StartTime;
            if (remaining <= duration)
            {
                return segment.StartTime + remaining;
            }

            remaining -= duration;
        }

        return session.Segments[^1].EndTime;
    }

    public static TimeSpan ToOffset(RecordingSession session, DateTimeOffset timestamp)
    {
        var elapsed = TimeSpan.Zero;
        foreach (var segment in session.Segments.OrderBy(static segment => segment.StartTime))
        {
            if (timestamp <= segment.StartTime)
            {
                return elapsed;
            }

            if (timestamp < segment.EndTime)
            {
                return elapsed + (timestamp - segment.StartTime);
            }

            elapsed += segment.EndTime - segment.StartTime;
        }

        return session.Duration;
    }

    public static IReadOnlyList<TimeSpan> GetSegmentBoundaries(RecordingSession session)
    {
        var boundaries = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;
        foreach (var segment in session.Segments.Take(session.Segments.Count - 1))
        {
            elapsed += segment.EndTime - segment.StartTime;
            boundaries.Add(elapsed);
        }

        return boundaries;
    }
}
