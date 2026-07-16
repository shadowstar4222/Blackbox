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

    [Fact]
    public void Playback_mapping_selects_the_next_segment_at_an_exact_boundary()
    {
        var session = CreateThreeSegmentSession();

        var before = RecordingTimeline.LocateSegment(session, TimeSpan.FromSeconds(9.5));
        var boundary = RecordingTimeline.LocateSegment(session, TimeSpan.FromSeconds(10));
        var afterEnd = RecordingTimeline.LocateSegment(session, TimeSpan.FromMinutes(2));

        Assert.Equal(new PlaybackSegmentPosition(0, TimeSpan.FromSeconds(9.5)), before);
        Assert.Equal(new PlaybackSegmentPosition(1, TimeSpan.Zero), boundary);
        Assert.Equal(new PlaybackSegmentPosition(2, TimeSpan.FromSeconds(10)), afterEnd);
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)],
            RecordingTimeline.GetSegmentStartOffsets(session));
    }

    [Fact]
    public void Playback_mapping_clamps_negative_and_past_end_offsets()
    {
        var session = CreateThreeSegmentSession();

        Assert.Equal(
            new PlaybackSegmentPosition(0, TimeSpan.Zero),
            RecordingTimeline.LocateSegment(session, TimeSpan.FromSeconds(-1)));
        Assert.Equal(
            new PlaybackSegmentPosition(2, TimeSpan.FromSeconds(10)),
            RecordingTimeline.LocateSegment(session, TimeSpan.FromMinutes(2)));
    }

    private static RecordingSession CreateThreeSegmentSession()
    {
        var start = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var first = TestSegments.Create("C:\\one.mkv", start, TimeSpan.FromSeconds(10));
        var second = TestSegments.Create("C:\\two.mkv", start.AddSeconds(10), TimeSpan.FromSeconds(10)) with
        {
            SessionId = first.SessionId
        };
        var third = TestSegments.Create("C:\\three.mkv", start.AddSeconds(20), TimeSpan.FromSeconds(10)) with
        {
            SessionId = first.SessionId
        };
        return new RecordingSession(
            first.SessionId,
            first.StartTime,
            third.EndTime,
            "Recording",
            [third, first, second],
            false,
            false);
    }
}
