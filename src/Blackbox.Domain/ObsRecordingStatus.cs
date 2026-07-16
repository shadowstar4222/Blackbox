namespace Blackbox.Domain;

public sealed record ObsRecordingStatus(
    bool IsActive,
    bool IsPaused,
    TimeSpan Duration,
    long BytesWritten);
