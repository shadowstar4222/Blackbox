using Blackbox.Domain;

namespace Blackbox.App;

public sealed record DiagnosticsSnapshot(
    bool IsRecording,
    bool IsAutomaticCaptureEnabled,
    int IndexedSegments,
    int DamagedSegments,
    int MissingSegments,
    long RecordingBytes,
    int PreservedRecoveryFiles,
    string RecoverySummary,
    IReadOnlyList<DiagnosticLogEntry> LogEntries);
