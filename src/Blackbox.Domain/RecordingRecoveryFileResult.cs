namespace Blackbox.Domain;

public sealed record RecordingRecoveryFileResult(
    string FilePath,
    RecordingRecoveryFileStatus Status,
    string? Detail = null,
    string? PreservedOriginalPath = null);
