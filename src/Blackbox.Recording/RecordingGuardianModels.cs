using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Recording;

public enum RecordingGuardianState
{
    Stopped,
    Waiting,
    Checking,
    Healthy,
    Warning,
    Repairing,
    Faulted
}

[Flags]
public enum RecordingGuardianIssue
{
    None = 0,
    ObsUnavailable = 1 << 0,
    MissingSource = 1 << 1,
    InactiveSource = 1 << 2,
    WrongWindow = 1 << 3,
    BlackFrame = 1 << 4,
    FrozenFrame = 1 << 5,
    SilentGameAudio = 1 << 6,
    RecordingStopped = 1 << 7,
    PreviewUnavailable = 1 << 8
}

public enum RecordingGuardianRepairAction
{
    None,
    RefreshCapture,
    RestartRecordingOutput
}

public sealed record RecordingFrameAnalysis(
    int Width,
    int Height,
    double MeanLuminance,
    double DarkPixelRatio,
    string Fingerprint);

public interface IRecordingFrameAnalyzer
{
    RecordingFrameAnalysis Analyze(ObsPreviewFrame frame);
}

public sealed record RecordingGuardianSnapshot(
    DateTimeOffset CapturedAt,
    RecordingGuardianState State,
    RecordingGuardianIssue Issue,
    string Message,
    string? TargetTitle,
    int OutputWidth,
    int OutputHeight,
    double FramesPerSecond,
    bool RecordingOutputActive,
    double? GameAudioPeakDb,
    int GameAudioSampleCount,
    RecordingFrameAnalysis? Frame,
    ObsPreviewFrame? Preview,
    int RepairAttempts,
    string? Evidence)
{
    public bool MicrophoneCaptureEnabled { get; init; }
    public bool MicrophonePacketReceived { get; init; }
    public long MicrophonePacketCount { get; init; }

    public static RecordingGuardianSnapshot Stopped(DateTimeOffset timestamp) => new(
        timestamp,
        RecordingGuardianState.Stopped,
        RecordingGuardianIssue.None,
        "Recording Guardian is stopped.",
        null,
        0,
        0,
        0,
        false,
        null,
        0,
        null,
        null,
        0,
        null);
}

public sealed record RecordingGuardianEvent(
    DateTimeOffset Timestamp,
    DiagnosticSeverity Severity,
    RecordingGuardianIssue Issue,
    string Message,
    string ActiveProfile,
    string? Evidence,
    string? Action,
    string? Result);

public sealed record RecordingGuardianDecision(
    RecordingGuardianIssue Issue,
    string Message,
    string Evidence,
    RecordingGuardianRepairAction RepairAction,
    bool IsFault,
    int HealthySampleCount);
