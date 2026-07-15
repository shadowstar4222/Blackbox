namespace Blackbox.Domain;

public sealed record MediaFileProbeResult(
    TimeSpan Duration,
    string VideoCodec,
    string PixelFormat,
    int Width,
    int Height,
    decimal FrameRate,
    bool IsHdr,
    IReadOnlyList<string> AudioTrackTitles);
