using Blackbox.Domain;
using Blackbox.Recording;

namespace Blackbox.App;

public sealed record DiagnosticsSnapshot(
    bool IsRecording,
    bool IsAutomaticCaptureEnabled,
    RecordingGuardianState GuardianState,
    string GuardianSummary,
    int IndexedSegments,
    int DamagedSegments,
    int MissingSegments,
    long RecordingBytes,
    int PreservedRecoveryFiles,
    string RecoverySummary,
    IReadOnlyList<DiagnosticLogEntry> LogEntries)
{
    public string RecorderSummary { get; init; } = "Recorder status has not been checked.";
}
