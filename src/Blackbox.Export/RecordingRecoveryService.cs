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
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly Dictionary<string, (RecordingFileState State, RecordingRecoveryFileResult Result)> _checkedFiles =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<RecordingRecoveryResult> RecoverAsync(
        IProgress<RecordingRecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _recoveryGate.WaitAsync(cancellationToken);
        try
        {
            return await RecoverCoreAsync(progress, cancellationToken);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task<RecordingRecoveryResult> RecoverCoreAsync(
        IProgress<RecordingRecoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        recordingSettings.Validate();
        options.Validate();
        var startedAt = clock.UtcNow;
        Directory.CreateDirectory(recordingSettings.RecordingLocation);
        await segmentRepository.InitializeAsync(cancellationToken);

        progress?.Report(new RecordingRecoveryProgress("Reconciling the recording database...", 5));
        var indexed = (await segmentRepository.GetAllAsync(cancellationToken))
            .ToDictionary(static segment => Path.GetFullPath(segment.FilePath), StringComparer.OrdinalIgnoreCase);
        var missingCount = indexed.Values.Count(static segment => !File.Exists(segment.FilePath));
        await segmentRepository.ReconcileMissingFilesAsync(cancellationToken);

        var firstSnapshots = RecordingFileState.Enumerate(recordingSettings.RecordingLocation, includeActive: true)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(RecordingFileState.Capture)
            .Where(static snapshot => snapshot is not null)
            .Cast<RecordingFileState>()
            .ToArray();
        if (options.StabilityObservationDelay > TimeSpan.Zero &&
            firstSnapshots.Any(snapshot => GetKnownResult(snapshot, indexed) is null))
        {
            await Task.Delay(options.StabilityObservationDelay, cancellationToken);
        }

        var results = new List<RecordingRecoveryFileResult>(firstSnapshots.Length);
        FfmpegInstallation? installation = null;
        for (var index = 0; index < firstSnapshots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = firstSnapshots[index];
            if (GetKnownResult(snapshot, indexed) is { } known)
            {
                results.Add(known);
                _checkedFiles[snapshot.Path] = (snapshot, known);
                continue;
            }

            var percent = 10 + (index + 1) * 85d / Math.Max(1, firstSnapshots.Length);
            progress?.Report(new RecordingRecoveryProgress(
                $"Checking {Path.GetFileName(snapshot.Path)}...",
                percent));
            var current = RecordingFileState.Capture(snapshot.Path);
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

            var native = NativeRecordingFile.TryParse(snapshot.Path);
            using var sessionLease = native is { IsActive: true } ? TryAcquireSessionLease(native.LockPath) : null;
            using var readLease = RecordingFileState.TryOpenReadLease(snapshot.Path);
            var finalizedPath = native is { IsActive: true }
                ? snapshot.Path[..^".active.mkv".Length] + ".mkv"
                : snapshot.Path;
            if (readLease is null || native is { IsActive: true } && sessionLease is null ||
                RecordingFileState.Capture(snapshot.Path) != snapshot ||
                native is { IsActive: true } && File.Exists(finalizedPath))
            {
                results.Add(new RecordingRecoveryFileResult(snapshot.Path,
                    RecordingRecoveryFileStatus.SkippedActive,
                    "The recording is in use or its finalized name already exists; the original was left untouched."));
                continue;
            }

            try
            {
                await mediaProbe.ProbeAsync(snapshot.Path, cancellationToken);
                if (native is { IsActive: true })
                {
                    File.Move(snapshot.Path, finalizedPath);
                }

                var healthy = new RecordingRecoveryFileResult(finalizedPath,
                    native is { IsActive: true } ? RecordingRecoveryFileStatus.Recovered : RecordingRecoveryFileStatus.Healthy);
                results.Add(healthy);
                Remember(finalizedPath, healthy);
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
                        finalizedPath,
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

            Remember(snapshot.Path, results[^1]);
        }

        var presentPaths = firstSnapshots.Select(static snapshot => snapshot.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _checkedFiles.Keys.Where(path => !presentPaths.Contains(path)).ToArray())
        {
            _checkedFiles.Remove(path);
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
        string finalizedPath,
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
            if (originalPath.Equals(finalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Replace(temporaryPath, originalPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                // Publish without overwriting anything. Keep the orphan's original bytes in place.
                File.Move(temporaryPath, finalizedPath);
                backupPath = originalPath;
            }
            logger.LogInformation(
                "Recovered recording {RecordingPath}; the original was preserved at {BackupPath}.",
                originalPath,
                backupPath);
            return new RecordingRecoveryFileResult(
                finalizedPath,
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

    private RecordingRecoveryFileResult? GetKnownResult(
        RecordingFileState snapshot,
        IReadOnlyDictionary<string, RecordingSegment> indexed)
    {
        if (NativeRecordingFile.TryParse(snapshot.Path) is { IsActive: true })
        {
            return _checkedFiles.TryGetValue(snapshot.Path, out var active) &&
                active.State == snapshot && active.Result.Status == RecordingRecoveryFileStatus.Damaged
                    ? active.Result
                    : null;
        }

        if (_checkedFiles.TryGetValue(snapshot.Path, out var cached))
        {
            return cached.State == snapshot ? cached.Result : null;
        }

        return indexed.TryGetValue(snapshot.Path, out var segment) &&
            !segment.IsDamaged && segment.FileSizeBytes == snapshot.Length
                ? new RecordingRecoveryFileResult(snapshot.Path, RecordingRecoveryFileStatus.Healthy,
                    "Unchanged indexed recording; no media probe was needed.")
                : null;
    }

    private void Remember(string path, RecordingRecoveryFileResult result)
    {
        if (RecordingFileState.Capture(path) is { } state)
        {
            _checkedFiles[path] = (state, result.Status == RecordingRecoveryFileStatus.Recovered
                ? new RecordingRecoveryFileResult(path, RecordingRecoveryFileStatus.Healthy)
                : result);
        }
    }

    private static FileStream? TryAcquireSessionLease(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
