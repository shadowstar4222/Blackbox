namespace Blackbox.Domain;

public sealed record RecordingRecoveryResult(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ReconciledMissingFiles,
    IReadOnlyList<RecordingRecoveryFileResult> Files)
{
    public int HealthyFiles => Files.Count(static file => file.Status == RecordingRecoveryFileStatus.Healthy);
    public int RecoveredFiles => Files.Count(static file => file.Status == RecordingRecoveryFileStatus.Recovered);
    public int DamagedFiles => Files.Count(static file => file.Status == RecordingRecoveryFileStatus.Damaged);
    public int SkippedActiveFiles => Files.Count(static file => file.Status == RecordingRecoveryFileStatus.SkippedActive);
}
