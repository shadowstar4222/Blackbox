using Blackbox.Domain;

namespace Blackbox.Tests;

internal static class TestSegments
{
    public static RecordingSegment Create(string filePath)
    {
        var metadata = Metadata();
        return new RecordingSegment(
            Guid.NewGuid(),
            metadata.SessionId,
            metadata.StartTime,
            metadata.EndTime,
            metadata.GameExecutable,
            metadata.GameTitle,
            metadata.VideoFormat,
            metadata.AudioTrackLayout,
            metadata.Encoder,
            metadata.Width,
            metadata.Height,
            metadata.FrameRate,
            metadata.IsHdr,
            false,
            filePath,
            1024);
    }

    public static SegmentMetadata Metadata()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);
        return new SegmentMetadata(
            Guid.NewGuid(),
            start,
            start.AddMinutes(2),
            "game.exe",
            "Test Game",
            "NV12",
            "1:full_mix;2:game;3:voice;4:raw_mic;5:processed_mic",
            "h264_nvenc",
            1920,
            1080,
            60,
            false);
    }
}
