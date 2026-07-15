namespace Blackbox.Domain;

public sealed record SegmentMetadata(
    Guid SessionId,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string GameExecutable,
    string GameTitle,
    string VideoFormat,
    string AudioTrackLayout,
    string Encoder,
    int Width,
    int Height,
    decimal FrameRate,
    bool IsHdr);
