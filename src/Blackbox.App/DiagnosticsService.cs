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
    RecordingGuardianService recordingGuardian,
    StartupRecoveryState recoveryState,
    DiagnosticLogOptions logOptions,
    SupportBundleService supportBundleService,
    IObsController recorder,
    IObsCaptureHealthClient healthClient)
{
    public string LogDirectory => logOptions.LogDirectory;
    public string SupportBundlePrivacyDisclosure => SupportBundleService.PrivacyDisclosure;
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
        var guardianSnapshot = recordingGuardian.Snapshot;
        var guardianEntries = recordingGuardian.History.Select(static entry =>
            new DiagnosticLogEntry(
                entry.Timestamp,
                DiagnosticCategory.Recording,
                entry.Severity,
                BuildGuardianLogMessage(entry),
                "RecordingGuardian"));
        var recentLogs = await logReader.GetRecentAsync(cancellationToken: cancellationToken);
        var recorderSummary = "Legacy recording engine; see Recording Guardian for health.";
        if (recorder is INativeRecorderController native)
        {
            try
            {
                var health = await healthClient.CaptureAsync(native.Target, includePreview: false, cancellationToken);
                recorderSummary = $"{native.Description}. " +
                    $"Output: {(health.RecordingOutputActive ? "recording" : "inactive")}. " +
                    $"Target: {native.Target?.Title ?? "none selected"}. " +
                    $"{health.Evidence} " +
                    (!health.SupportsPreview ? "Preview analysis unavailable. " : string.Empty) +
                    (!health.SupportsAudioMeter ? "Audio health meter unavailable." : string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                recorderSummary = $"Native recorder health check failed: {ex.Message}";
            }
        }

        var combinedLogs = guardianEntries
            .Concat(recentLogs)
            .OrderByDescending(static entry => entry.Timestamp)
            .Take(300)
            .ToArray();
        return new DiagnosticsSnapshot(
            recordingCoordinator.IsRecording,
            automaticCapture.IsEnabled,
            guardianSnapshot.State,
            guardianSnapshot.Message,
            segments.Count,
            segments.Count(static segment => segment.IsDamaged),
            segments.Count(static segment => !File.Exists(segment.FilePath)),
            existing.Sum(static segment => segment.FileSizeBytes),
            preservedFiles,
            recoverySummary,
            combinedLogs)
        {
            RecorderSummary = recorderSummary
        };
    }

    public async Task<SupportBundleResult> ExportSupportBundleAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return await supportBundleService.ExportAsync(
            destinationPath,
            new SupportBundleRequest(
                snapshot.IsRecording,
                snapshot.IsAutomaticCaptureEnabled,
                snapshot.IndexedSegments,
                snapshot.DamagedSegments,
                snapshot.MissingSegments,
                snapshot.RecordingBytes,
                snapshot.PreservedRecoveryFiles,
                $"{snapshot.RecoverySummary}. {snapshot.RecorderSummary}",
                snapshot.LogEntries),
            cancellationToken);
    }

    private static string BuildGuardianLogMessage(RecordingGuardianEvent entry)
    {
        var details = new[]
        {
            $"Profile: {entry.ActiveProfile}",
            entry.Evidence is null ? null : $"Evidence: {entry.Evidence}",
            entry.Action is null ? null : $"Action: {entry.Action}",
            entry.Result is null ? null : $"Result: {entry.Result}"
        }.Where(static value => value is not null);
        var suffix = string.Join(" ", details);
        return string.IsNullOrEmpty(suffix) ? entry.Message : $"{entry.Message} {suffix}";
    }
}
