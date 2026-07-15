using Blackbox.Domain;

namespace Blackbox.Tests;

internal static class TestSegments
{
    public static RecordingSegment Create(
        string filePath,
        DateTimeOffset? startTime = null,
        TimeSpan? duration = null,
        long fileSizeBytes = 1024,
        bool isProtected = false)
    {
        var metadata = Metadata(startTime, duration);
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
            isProtected,
            filePath,
            fileSizeBytes);
    }

    public static SegmentMetadata Metadata(DateTimeOffset? startTime = null, TimeSpan? duration = null)
    {
        var start = startTime ?? DateTimeOffset.UtcNow.AddMinutes(-2);
        return new SegmentMetadata(
            Guid.NewGuid(),
            start,
            start.Add(duration ?? TimeSpan.FromMinutes(2)),
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
