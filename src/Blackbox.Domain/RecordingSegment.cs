namespace Blackbox.Domain;

public sealed record RecordingSegment(
    Guid Id,
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
    bool IsHdr,
    bool IsProtected,
    string FilePath,
    long FileSizeBytes,
    bool IsDamaged = false,
    string? DamageDetail = null);
