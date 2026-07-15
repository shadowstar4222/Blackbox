using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class RecordingTimelineTests
{
    [Fact]
    public void Timeline_mapping_uses_playable_segment_durations_across_wall_clock_overlap()
    {
        var start = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var first = TestSegments.Create("C:\\one.mkv", start, TimeSpan.FromSeconds(60.1));
        var second = TestSegments.Create("C:\\two.mkv", start.AddMinutes(1), TimeSpan.FromSeconds(60.1)) with
        {
            SessionId = first.SessionId
        };
        var session = new RecordingSession(
            first.SessionId,
            first.StartTime,
            second.EndTime,
            "Recording",
            [first, second],
            false,
            false);

        var timestamp = RecordingTimeline.ToTimestamp(session, TimeSpan.FromSeconds(70.1));
        var offset = RecordingTimeline.ToOffset(session, timestamp);

        Assert.Equal(second.StartTime.AddSeconds(10), timestamp);
        Assert.Equal(TimeSpan.FromSeconds(70.1), offset);
        Assert.Equal([TimeSpan.FromSeconds(60.1)], RecordingTimeline.GetSegmentBoundaries(session));
    }
}
