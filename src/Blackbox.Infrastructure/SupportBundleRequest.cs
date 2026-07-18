using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed record SupportBundleRequest(
    bool IsRecording,
    bool IsAutomaticCaptureEnabled,
    int IndexedSegments,
    int DamagedSegments,
    int MissingSegments,
    long RecordingBytes,
    int PreservedRecoveryFiles,
    string RecoverySummary,
    IReadOnlyList<DiagnosticLogEntry> LogEntries);
