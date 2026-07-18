using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class RecordingRecoveryService(
    RecordingSettings recordingSettings,
    ISegmentRepository segmentRepository,
    IMediaProbe mediaProbe,
    IFfmpegProvisioner ffmpegProvisioner,
    IFfmpegCommandRunner commandRunner,
    IClock clock,
    RecordingRecoveryOptions options,
    ILogger<RecordingRecoveryService> logger)
{
    public async Task<RecordingRecoveryResult> RecoverAsync(
        IProgress<RecordingRecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        recordingSettings.Validate();
        options.Validate();
        var startedAt = clock.UtcNow;
        Directory.CreateDirectory(recordingSettings.RecordingLocation);
        await segmentRepository.InitializeAsync(cancellationToken);

        progress?.Report(new RecordingRecoveryProgress("Reconciling the recording database...", 5));
        var missingCount = (await segmentRepository.GetAllAsync(cancellationToken))
            .Count(static segment => !File.Exists(segment.FilePath));
        await segmentRepository.ReconcileMissingFilesAsync(cancellationToken);

        var firstSnapshots = EnumerateCandidates(recordingSettings.RecordingLocation)
            .Select(FileSnapshot.Capture)
            .Where(static snapshot => snapshot is not null)
            .Cast<FileSnapshot>()
            .ToArray();
        if (options.StabilityObservationDelay > TimeSpan.Zero && firstSnapshots.Length > 0)
        {
            await Task.Delay(options.StabilityObservationDelay, cancellationToken);
        }

        var results = new List<RecordingRecoveryFileResult>(firstSnapshots.Length);
        FfmpegInstallation? installation = null;
        for (var index = 0; index < firstSnapshots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = firstSnapshots[index];
            var percent = 10 + (index + 1) * 85d / Math.Max(1, firstSnapshots.Length);
            progress?.Report(new RecordingRecoveryProgress(
                $"Checking {Path.GetFileName(snapshot.Path)}...",
                percent));
            var current = FileSnapshot.Capture(snapshot.Path);
            if (current is null ||
                current.Length != snapshot.Length ||
                current.LastWriteTimeUtc != snapshot.LastWriteTimeUtc ||
                clock.UtcNow - current.LastWriteTimeUtc < options.MinimumFileAge)
            {
                results.Add(new RecordingRecoveryFileResult(
                    snapshot.Path,
                    RecordingRecoveryFileStatus.SkippedActive,
                    "The file is still changing and was left untouched."));
                continue;
            }

            try
            {
                await mediaProbe.ProbeAsync(snapshot.Path, cancellationToken);
                results.Add(new RecordingRecoveryFileResult(snapshot.Path, RecordingRecoveryFileStatus.Healthy));
                continue;
            }
            catch (Exception probeException) when (probeException is not OperationCanceledException)
            {
                logger.LogWarning(
                    probeException,
                    "Recording {RecordingPath} is unreadable; attempting container recovery.",
                    snapshot.Path);
                try
                {
                    installation ??= await ffmpegProvisioner.EnsureInstalledAsync(cancellationToken: cancellationToken);
                    results.Add(await RecoverFileAsync(
                        snapshot.Path,
                        probeException,
                        installation,
                        cancellationToken));
                }
                catch (Exception provisioningException) when (provisioningException is not OperationCanceledException)
                {
                    logger.LogError(
                        provisioningException,
                        "Recovery tools are unavailable for recording {RecordingPath}; the original was not changed.",
                        snapshot.Path);
                    results.Add(new RecordingRecoveryFileResult(
                        snapshot.Path,
                        RecordingRecoveryFileStatus.Damaged,
                        $"Initial read failed: {probeException.Message} Recovery tools failed: {provisioningException.Message}"));
                }
            }
        }

        var completedAt = clock.UtcNow;
        var result = new RecordingRecoveryResult(startedAt, completedAt, missingCount, results);
        progress?.Report(new RecordingRecoveryProgress(
            $"Recovery check complete: {result.RecoveredFiles} repaired, {result.DamagedFiles} damaged.",
            100));
        logger.LogInformation(
            "Startup recovery completed. Healthy={HealthyCount}, Recovered={RecoveredCount}, Damaged={DamagedCount}, ActiveSkipped={ActiveSkippedCount}, MissingReconciled={MissingCount}.",
            result.HealthyFiles,
            result.RecoveredFiles,
            result.DamagedFiles,
            result.SkippedActiveFiles,
            result.ReconciledMissingFiles);
        return result;
    }

    private async Task<RecordingRecoveryFileResult> RecoverFileAsync(
        string originalPath,
        Exception probeException,
        FfmpegInstallation installation,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalPath);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(originalPath)!,
            $"{Path.GetFileNameWithoutExtension(originalPath)}.recovery-{Guid.NewGuid():N}.partial{extension}");
        try
        {
            await commandRunner.RunAsync(
                installation.FfmpegPath,
                BuildRecoveryArguments(originalPath, temporaryPath),
                TimeSpan.Zero,
                cancellationToken: cancellationToken);
            var temporaryFile = new FileInfo(temporaryPath);
            if (!temporaryFile.Exists || temporaryFile.Length == 0)
            {
                throw new InvalidDataException("FFmpeg did not produce a recovered recording.");
            }

            await mediaProbe.ProbeAsync(temporaryPath, cancellationToken);
            var backupDirectory = GetBackupDirectory();
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(
                backupDirectory,
                $"{Path.GetFileNameWithoutExtension(originalPath)}.unrecovered-{clock.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
            File.Replace(temporaryPath, originalPath, backupPath, ignoreMetadataErrors: true);
            logger.LogInformation(
                "Recovered recording {RecordingPath}; the original was preserved at {BackupPath}.",
                originalPath,
                backupPath);
            return new RecordingRecoveryFileResult(
                originalPath,
                RecordingRecoveryFileStatus.Recovered,
                "The media container was rebuilt and validated.",
                backupPath);
        }
        catch (Exception recoveryException) when (recoveryException is not OperationCanceledException)
        {
            var attemptPath = PreserveFailedAttempt(temporaryPath, originalPath);
            var detail = $"Initial read failed: {probeException.Message} Recovery failed: {recoveryException.Message}";
            logger.LogError(
                recoveryException,
                "Could not recover recording {RecordingPath}. The original was not replaced. AttemptPath={AttemptPath}.",
                originalPath,
                attemptPath);
            return new RecordingRecoveryFileResult(
                originalPath,
                RecordingRecoveryFileStatus.Damaged,
                detail,
                attemptPath);
        }
    }

    private string? PreserveFailedAttempt(string temporaryPath, string originalPath)
    {
        try
        {
            var temporary = new FileInfo(temporaryPath);
            if (!temporary.Exists || temporary.Length == 0)
            {
                if (temporary.Exists)
                {
                    temporary.Delete();
                }

                return null;
            }

            var backupDirectory = GetBackupDirectory();
            Directory.CreateDirectory(backupDirectory);
            var attemptPath = Path.Combine(
                backupDirectory,
                $"{Path.GetFileNameWithoutExtension(originalPath)}.failed-recovery-{clock.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{Path.GetExtension(originalPath)}");
            File.Move(temporaryPath, attemptPath);
            return attemptPath;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not preserve failed recovery output {TemporaryPath}.", temporaryPath);
            return temporaryPath;
        }
    }

    private string GetBackupDirectory() =>
        Path.Combine(recordingSettings.RecordingLocation, options.BackupDirectoryName);

    private static IReadOnlyList<string> BuildRecoveryArguments(string inputPath, string outputPath) =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-fflags", "+genpts+discardcorrupt",
        "-err_detect", "ignore_err",
        "-i", inputPath,
        "-map", "0",
        "-c", "copy",
        "-map_metadata", "0",
        "-progress", "pipe:1",
        "-nostats",
        "-y",
        outputPath
    ];

    private static IEnumerable<string> EnumerateCandidates(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path =>
                !RecordingDirectoryLayout.IsInternalPath(directory, path) &&
                !path.Contains(".partial", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains(".active", StringComparison.OrdinalIgnoreCase) &&
                (Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);

    private sealed record FileSnapshot(string Path, long Length, DateTimeOffset LastWriteTimeUtc)
    {
        public static FileSnapshot? Capture(string path)
        {
            try
            {
                var file = new FileInfo(path);
                file.Refresh();
                return file.Exists && file.Length > 0
                    ? new FileSnapshot(file.FullName, file.Length, file.LastWriteTimeUtc)
                    : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
