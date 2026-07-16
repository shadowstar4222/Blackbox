using System.IO;
using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Infrastructure;
using Blackbox.Recording;

namespace Blackbox.App;

public sealed class DiagnosticsService(
    ISegmentRepository segmentRepository,
    IDiagnosticLogReader logReader,
    RecordingSettings recordingSettings,
    RecordingRecoveryOptions recoveryOptions,
    RecordingCoordinator recordingCoordinator,
    AutomaticCaptureService automaticCapture,
    StartupRecoveryState recoveryState,
    DiagnosticLogOptions logOptions)
{
    public string LogDirectory => logOptions.LogDirectory;
    public string RecoveryBackupDirectory =>
        Path.Combine(recordingSettings.RecordingLocation, recoveryOptions.BackupDirectoryName);

    public async Task<DiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await segmentRepository.InitializeAsync(cancellationToken);
        var segments = await segmentRepository.GetAllAsync(cancellationToken);
        var existing = segments.Where(static segment => File.Exists(segment.FilePath)).ToArray();
        var preservedFiles = Directory.Exists(RecoveryBackupDirectory)
            ? Directory.EnumerateFiles(RecoveryBackupDirectory, "*", SearchOption.TopDirectoryOnly).Count()
            : 0;
        var outcome = recoveryState.LastOutcome;
        var recoverySummary = outcome is null
            ? "No startup recovery result is available yet."
            : $"{outcome.RecordingRecovery.RecoveredFiles} repaired, " +
              $"{outcome.RecordingRecovery.DamagedFiles} damaged, " +
              $"{outcome.RecordingRecovery.ReconciledMissingFiles} missing reconciled";
        return new DiagnosticsSnapshot(
            recordingCoordinator.IsRecording,
            automaticCapture.IsEnabled,
            segments.Count,
            segments.Count(static segment => segment.IsDamaged),
            segments.Count(static segment => !File.Exists(segment.FilePath)),
            existing.Sum(static segment => segment.FileSizeBytes),
            preservedFiles,
            recoverySummary,
            await logReader.GetRecentAsync(cancellationToken: cancellationToken));
    }
}
