using Blackbox.Domain;

namespace Blackbox.Export;

public sealed record RecordingRecoveryOptions
{
    public TimeSpan StabilityObservationDelay { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan MinimumFileAge { get; init; } = TimeSpan.FromSeconds(1);
    public string BackupDirectoryName { get; init; } = RecordingDirectoryLayout.RecoveryBackupName;

    public void Validate()
    {
        if (StabilityObservationDelay < TimeSpan.Zero ||
            MinimumFileAge < TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(BackupDirectoryName) ||
            BackupDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("Recording recovery settings are invalid.");
        }
    }
}
