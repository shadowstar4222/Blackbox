using Blackbox.Domain;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Blackbox.Tests;

public sealed class PlaybackTimelineIndexTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(9.5, 0, 9.5)]
    [InlineData(10, 1, 0)]
    [InlineData(29.5, 1, 19.5)]
    [InlineData(30, 2, 0)]
    [InlineData(45, 2, 15)]
    [InlineData(100, 2, 15)]
    public void Locates_segment_without_sorting_each_lookup(double seconds, int segmentIndex, double localSeconds)
    {
        var start = DateTimeOffset.UtcNow;
        var segments = new[] { 10, 20, 15 }.Select((duration, index) =>
            TestSegments.Create($"segment-{index}.mkv", startTime: start.AddSeconds(index == 0 ? 0 : index == 1 ? 10 : 30), duration: TimeSpan.FromSeconds(duration))).ToArray();
        var session = new RecordingSession(Guid.NewGuid(), start, start.AddSeconds(45), "Test", segments.Reverse().ToArray(), false, false);
        var timeline = new PlaybackTimelineIndex(session);
        var position = timeline.Locate(TimeSpan.FromSeconds(seconds));
        Assert.Equal(segmentIndex, position.SegmentIndex);
        Assert.Equal(TimeSpan.FromSeconds(localSeconds), position.SegmentOffset);
        Assert.Equal(TimeSpan.FromSeconds(45), timeline.Duration);
    }

    [Fact]
    public void Repeated_lookup_does_not_allocate_segment_arrays()
    {
        var segment = TestSegments.Create("one.mkv", duration: TimeSpan.FromMinutes(1));
        var session = new RecordingSession(segment.SessionId, segment.StartTime, segment.EndTime, "Test", [segment], false, false);
        var timeline = new PlaybackTimelineIndex(session);
        var segments = timeline.Segments;
        var starts = timeline.StartOffsets;
        for (var index = 0; index < 10000; index++) timeline.Locate(TimeSpan.FromSeconds(index % 60));
        Assert.Same(segments, timeline.Segments);
        Assert.Same(starts, timeline.StartOffsets);
    }

    [Fact]
    public void Empty_timeline_is_rejected()
    {
        var session = new RecordingSession(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Test", [], false, false);
        Assert.Throws<ArgumentException>(() => new PlaybackTimelineIndex(session));
    }

    [Fact]
    public void Large_timeline_lookup_is_allocation_free()
    {
        var origin = DateTimeOffset.UtcNow;
        var segments = Enumerable.Range(0, 2000).Select(index =>
            TestSegments.Create($"{index}.mkv", origin.AddMinutes(index), TimeSpan.FromMinutes(1))).ToArray();
        var session = new RecordingSession(Guid.NewGuid(), origin, origin.AddMinutes(2000), "Long session", segments, false, false);
        var timeline = new PlaybackTimelineIndex(session);
        for (var index = 0; index < 100; index++) timeline.Locate(TimeSpan.FromSeconds(index));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < 2000; index++) timeline.Locate(TimeSpan.FromSeconds(index * 31));
        var indexedTime = Stopwatch.GetElapsedTime(started);
        var indexedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (var index = 0; index < 2000; index++) RecordingTimeline.LocateSegment(session, TimeSpan.FromSeconds(index * 31));
        var originalTime = Stopwatch.GetElapsedTime(started);
        var originalBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"2000 lookups / 2000 segments: original={originalTime.TotalMilliseconds:F3} ms, {originalBytes} bytes; indexed={indexedTime.TotalMilliseconds:F3} ms, {indexedBytes} bytes.");
        Assert.Equal(0, indexedBytes);
        Assert.True(originalBytes > indexedBytes);
    }
}
