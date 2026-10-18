using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public interface IObsCaptureHealthClient
{
    Task<ObsCaptureHealthSample> CaptureAsync(
        GameCaptureTarget? target,
        bool includePreview,
        CancellationToken cancellationToken = default);
}

public sealed record ObsPreviewFrame(
    byte[] ImageBytes,
    string MediaType,
    DateTimeOffset CapturedAt);

public sealed record ObsCaptureHealthSample(
    DateTimeOffset CapturedAt,
    bool SourceExists,
    bool SourceActive,
    bool SourceMatchesTarget,
    string? BoundWindowIdentifier,
    string? CaptureMode,
    int? WindowMatchPriority,
    int OutputWidth,
    int OutputHeight,
    double FramesPerSecond,
    bool RecordingOutputActive,
    double? GameAudioPeakDb,
    int GameAudioSampleCount,
    ObsPreviewFrame? Preview,
    string? Evidence)
{
    public bool SupportsPreview { get; init; } = true;
    public bool SupportsAudioMeter { get; init; } = true;
    public bool PreviewSampled { get; init; } = true;
    public bool SupportsAutomaticCaptureRefresh { get; init; } = true;
    public bool MicrophoneCaptureEnabled { get; init; }
    public bool MicrophonePacketReceived { get; init; }
    public long MicrophonePacketCount { get; init; }
}
