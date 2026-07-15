using Blackbox.Domain;
using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class RecordingSessionCatalogTests
{
    [Fact]
    public void Build_presents_contiguous_segments_as_one_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "one.mkv");
        var secondPath = Path.Combine(root, "two.mkv");
        File.WriteAllText(firstPath, "one");
        File.WriteAllText(secondPath, "two");
        try
        {
            var sessionId = Guid.NewGuid();
            var start = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
            var segments = new[]
            {
                Segment(sessionId, firstPath, start, TimeSpan.FromMinutes(2)),
                Segment(sessionId, secondPath, start.AddMinutes(2), TimeSpan.FromMinutes(2))
            };

            var session = Assert.Single(RecordingSessionCatalog.Build(segments));

            Assert.Equal(TimeSpan.FromMinutes(4), session.Duration);
            Assert.Equal(2, session.Segments.Count);
            Assert.False(session.HasGaps);
            Assert.False(session.HasMissingSegments);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Duration_uses_the_playable_media_length_when_segment_timestamps_overlap()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "one.mkv");
        var secondPath = Path.Combine(root, "two.mkv");
        File.WriteAllText(firstPath, "one");
        File.WriteAllText(secondPath, "two");
        try
        {
            var sessionId = Guid.NewGuid();
            var start = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
            var segments = new[]
            {
                Segment(sessionId, firstPath, start, TimeSpan.FromSeconds(60.1)),
                Segment(sessionId, secondPath, start.AddMinutes(1), TimeSpan.FromSeconds(60.1))
            };

            var session = Assert.Single(RecordingSessionCatalog.Build(segments));

            Assert.Equal(TimeSpan.FromSeconds(120.2), session.Duration);
            Assert.False(session.HasGaps);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static RecordingSegment Segment(
        Guid sessionId,
        string path,
        DateTimeOffset start,
        TimeSpan duration) => new(
            Guid.NewGuid(),
            sessionId,
            start,
            start + duration,
            string.Empty,
            "Recording",
            "nv12",
            "1:Full listening mix;2:Game audio",
            "h264",
            1920,
            1080,
            60,
            false,
            false,
            path,
            new FileInfo(path).Length);
}
