namespace Blackbox.Domain;

public sealed class PlaybackTimelineIndex
{
    private readonly TimeSpan[] _starts;
    public IReadOnlyList<RecordingSegment> Segments { get; }
    public IReadOnlyList<TimeSpan> StartOffsets => _starts;
    public TimeSpan Duration { get; }

    public PlaybackTimelineIndex(RecordingSession session)
    {
        if (session.Segments.Count == 0) throw new ArgumentException("A playable timeline requires segments.", nameof(session));
        Segments = session.Segments.OrderBy(static segment => segment.StartTime).ToArray();
        _starts = new TimeSpan[Segments.Count];
        var elapsed = TimeSpan.Zero;
        for (var index = 0; index < Segments.Count; index++)
        {
            _starts[index] = elapsed;
            var duration = Segments[index].EndTime - Segments[index].StartTime;
            if (duration < TimeSpan.Zero) throw new ArgumentException("Segment duration cannot be negative.", nameof(session));
            elapsed += duration;
        }
        Duration = elapsed;
    }

    public PlaybackSegmentPosition Locate(TimeSpan offset)
    {
        offset = offset < TimeSpan.Zero ? TimeSpan.Zero : offset > Duration ? Duration : offset;
        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_starts[middle] <= offset) low = middle + 1;
            else high = middle;
        }
        var index = Math.Max(0, low - 1);
        return new PlaybackSegmentPosition(index, offset - _starts[index]);
    }
}
