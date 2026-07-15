using Blackbox.Domain;
using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class FfmpegExportArgumentBuilderTests
{
    [Fact]
    public void Build_uses_stream_copy_for_a_full_session()
    {
        var session = CreateSession(TimeSpan.FromMinutes(4));
        var request = new SessionExportRequest(session, TimeSpan.Zero, session.Duration, "C:\\Exports\\session.mkv");

        var arguments = FfmpegExportArgumentBuilder.Build(request, "list.ffconcat", "partial.mkv", out var streamCopy);

        Assert.True(streamCopy);
        Assert.Contains("copy", arguments);
        Assert.DoesNotContain("libx264", arguments);
        Assert.Contains("title=Full listening mix", arguments);
    }

    [Fact]
    public void Build_reencodes_a_trimmed_range_for_accurate_boundaries()
    {
        var session = CreateSession(TimeSpan.FromMinutes(4));
        var request = new SessionExportRequest(
            session,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(3),
            "C:\\Exports\\clip.mp4");

        var arguments = FfmpegExportArgumentBuilder.Build(request, "list.ffconcat", "partial.mp4", out var streamCopy);

        Assert.False(streamCopy);
        Assert.Contains("libx264", arguments);
        Assert.Contains("15", arguments);
        Assert.Contains("165", arguments);
        Assert.Contains("+faststart", arguments);
    }

    private static RecordingSession CreateSession(TimeSpan duration)
    {
        var start = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var segment = TestSegments.Create("C:\\Recordings\\one.mkv", start, duration);
        return new RecordingSession(segment.SessionId, start, start + duration, "Recording", [segment], false, false);
    }
}
