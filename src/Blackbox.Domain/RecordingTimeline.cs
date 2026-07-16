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
        foreach (var segment in session.Segments
                     .OrderBy(static segment => segment.StartTime)
                     .Take(session.Segments.Count - 1))
        {
            elapsed += segment.EndTime - segment.StartTime;
            boundaries.Add(elapsed);
        }

        return boundaries;
    }

    public static IReadOnlyList<TimeSpan> GetSegmentStartOffsets(RecordingSession session)
    {
        EnsureSegments(session);
        return new[] { TimeSpan.Zero }
            .Concat(GetSegmentBoundaries(session))
            .ToArray();
    }

    public static PlaybackSegmentPosition LocateSegment(RecordingSession session, TimeSpan offset)
    {
        EnsureSegments(session);
        var orderedSegments = session.Segments.OrderBy(static segment => segment.StartTime).ToArray();
        var remaining = offset < TimeSpan.Zero
            ? TimeSpan.Zero
            : offset > session.Duration
                ? session.Duration
                : offset;

        for (var index = 0; index < orderedSegments.Length; index++)
        {
            var duration = orderedSegments[index].EndTime - orderedSegments[index].StartTime;
            if (remaining < duration || index == orderedSegments.Length - 1)
            {
                return new PlaybackSegmentPosition(index, remaining > duration ? duration : remaining);
            }

            remaining -= duration;
        }

        throw new InvalidOperationException("The recording does not contain a playable segment.");
    }

    private static void EnsureSegments(RecordingSession session)
    {
        if (session.Segments.Count == 0)
        {
            throw new InvalidOperationException("The recording does not contain any segments.");
        }
    }
}

public sealed record PlaybackSegmentPosition(int SegmentIndex, TimeSpan SegmentOffset);
