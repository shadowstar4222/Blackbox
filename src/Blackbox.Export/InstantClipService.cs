using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class InstantClipService(
    IClipRepository clipRepository,
    RecordingSessionCatalog sessionCatalog,
    RecordingLibraryService recordingLibrary,
    SessionExportService exportService,
    InstantClipOptions options,
    IClock clock,
    ILogger<InstantClipService> logger,
    IRecordingTimelineClock? timelineClock = null) : IDisposable
{
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private DateTimeOffset _lastLibraryRefresh = DateTimeOffset.MinValue;
    private long _queueVersion;
    private long _emptyQueueVersion = -1;

    public event Action? ClipsChanged;

    public async Task<InstantClip> CreateAsync(
        TimeSpan duration,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Clip duration must be between 1 second and 30 minutes.");
        }

        var now = clock.UtcNow;
        var recordingTime = timelineClock?.RecordingTime ?? now;
        var clip = new InstantClip
        {
            Id = Guid.NewGuid(),
            RequestedAt = now,
            RequestedStartTime = recordingTime - duration,
            RequestedEndTime = recordingTime,
            Label = string.IsNullOrWhiteSpace(label)
                ? $"Instant clip {now:HH:mm:ss}"
                : label.Trim(),
            Status = InstantClipStatus.Pending,
            UpdatedAt = now
        };
        clip.Validate();
        await clipRepository.AddAsync(clip, cancellationToken);
        Interlocked.Increment(ref _queueVersion);
        ClipsChanged?.Invoke();
        logger.LogInformation(
            "Queued instant clip {ClipId} for the previous {ClipDuration}.",
            clip.Id,
            duration);
        return clip;
    }

    public Task<IReadOnlyList<InstantClip>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        clipRepository.GetAllAsync(cancellationToken);

    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Requests enter through Create/Retry. Discover persisted requests once after startup.
        if (Volatile.Read(ref _emptyQueueVersion) == Volatile.Read(ref _queueVersion))
        {
            return;
        }

        if (!await _processGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var queueVersion = Volatile.Read(ref _queueVersion);
            var pending = (await clipRepository.GetAllAsync(cancellationToken))
                .Where(static clip => clip.Status == InstantClipStatus.Pending)
                .OrderBy(static clip => clip.RequestedAt)
                .ToArray();
            if (pending.Length == 0)
            {
                Volatile.Write(ref _emptyQueueVersion, queueVersion);
                return;
            }

            IReadOnlyList<RecordingSession> sessions;
            if (clock.UtcNow - _lastLibraryRefresh >= options.LibraryRefreshInterval)
            {
                sessions = await recordingLibrary.RefreshAsync(cancellationToken: cancellationToken);
                _lastLibraryRefresh = clock.UtcNow;
            }
            else
            {
                sessions = await sessionCatalog.GetSessionsAsync(cancellationToken);
            }

            foreach (var clip in pending)
            {
                await ProcessClipAsync(clip, sessions, cancellationToken);
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task RetryAsync(Guid clipId, CancellationToken cancellationToken = default)
    {
        var clip = await RequireClipAsync(clipId, cancellationToken);
        if (clip.Status != InstantClipStatus.Failed)
        {
            throw new InvalidOperationException("Only failed clips can be retried.");
        }

        await clipRepository.ResetPendingAsync(clipId, clock.UtcNow, cancellationToken);
        Interlocked.Increment(ref _queueVersion);
        ClipsChanged?.Invoke();
        await ProcessPendingAsync(cancellationToken);
    }

    public async Task SetProtectedAsync(
        Guid clipId,
        bool isProtected,
        CancellationToken cancellationToken = default)
    {
        var clip = await RequireClipAsync(clipId, cancellationToken);
        if (clip.Status is not (InstantClipStatus.Ready or InstantClipStatus.Protected))
        {
            throw new InvalidOperationException("Only ready clips can be protected.");
        }

        await clipRepository.SetStatusAsync(
            clipId,
            isProtected ? InstantClipStatus.Protected : InstantClipStatus.Ready,
            clock.UtcNow,
            cancellationToken);
        ClipsChanged?.Invoke();
    }

    public async Task MarkExportedAsync(
        Guid clipId,
        CancellationToken cancellationToken = default)
    {
        var clip = await RequireClipAsync(clipId, cancellationToken);
        if (!clip.IsPlayable)
        {
            throw new InvalidOperationException("Only finished clips can be marked exported.");
        }

        await clipRepository.SetStatusAsync(
            clipId,
            InstantClipStatus.Exported,
            clock.UtcNow,
            cancellationToken);
        ClipsChanged?.Invoke();
    }

    public async Task DiscardAsync(Guid clipId, CancellationToken cancellationToken = default)
    {
        var clip = await RequireClipAsync(clipId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(clip.OutputPath))
        {
            TryDelete(clip.OutputPath);
        }

        await clipRepository.DeleteAsync(clipId, cancellationToken);
        ClipsChanged?.Invoke();
        logger.LogInformation("Discarded instant clip {ClipId}.", clipId);
    }

    private async Task ProcessClipAsync(
        InstantClip clip,
        IReadOnlyList<RecordingSession> sessions,
        CancellationToken cancellationToken)
    {
        var session = sessions
            .Where(session =>
                session.StartTime < clip.RequestedEndTime &&
                session.EndTime > clip.RequestedStartTime &&
                session.EndTime + options.EndTimeTolerance >= clip.RequestedEndTime)
            .OrderByDescending(static session => session.EndTime)
            .FirstOrDefault();
        if (session is null)
        {
            if (clock.UtcNow - clip.RequestedAt >= options.PendingTimeout)
            {
                await clipRepository.MarkFailedAsync(
                    clip.Id,
                    "The requested footage did not become available before the clip timed out.",
                    clock.UtcNow,
                    cancellationToken);
                ClipsChanged?.Invoke();
            }

            return;
        }

        var actualStart = clip.RequestedStartTime < session.StartTime
            ? session.StartTime
            : clip.RequestedStartTime;
        var actualEnd = clip.RequestedEndTime > session.EndTime
            ? session.EndTime
            : clip.RequestedEndTime;
        var sourceSegments = session.Segments
            .Where(segment => segment.StartTime < actualEnd && segment.EndTime > actualStart)
            .ToArray();
        if (sourceSegments.Length == 0 || actualEnd <= actualStart)
        {
            await clipRepository.MarkFailedAsync(
                clip.Id,
                "No readable recording segments overlap the requested clip range.",
                clock.UtcNow,
                cancellationToken);
            ClipsChanged?.Invoke();
            return;
        }

        await clipRepository.BeginFinalizationAsync(
            clip.Id,
            session.Id,
            actualStart,
            actualEnd,
            sourceSegments.Select(static segment => segment.Id).ToArray(),
            cancellationToken);
        var destinationPath = GetDestinationPath(clip, session);
        try
        {
            await exportService.ExportAsync(
                new SessionExportRequest(
                    session,
                    RecordingTimeline.ToOffset(session, actualStart),
                    RecordingTimeline.ToOffset(session, actualEnd),
                    destinationPath),
                cancellationToken: cancellationToken);
            await clipRepository.MarkReadyAsync(
                clip.Id,
                session.GameTitle,
                destinationPath,
                clock.UtcNow,
                cancellationToken);
            ClipsChanged?.Invoke();
            logger.LogInformation(
                "Instant clip {ClipId} is ready at {OutputPath}.",
                clip.Id,
                destinationPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await clipRepository.MarkFailedAsync(
                clip.Id,
                ex.Message,
                clock.UtcNow,
                cancellationToken);
            ClipsChanged?.Invoke();
            logger.LogError(ex, "Instant clip {ClipId} failed.", clip.Id);
        }
    }

    private async Task<InstantClip> RequireClipAsync(
        Guid clipId,
        CancellationToken cancellationToken) =>
        await clipRepository.GetByIdAsync(clipId, cancellationToken)
        ?? throw new InvalidOperationException("The instant clip no longer exists.");

    private string GetDestinationPath(InstantClip clip, RecordingSession session)
    {
        var gameDirectory = SanitizeName(session.GameTitle);
        var dateDirectory = clip.RequestedAt.ToLocalTime().ToString("yyyy-MM-dd");
        var fileName =
            $"{clip.RequestedAt.ToLocalTime():HH-mm-ss}-{clip.Id.ToString("N")[..8]} - " +
            $"{SanitizeName(clip.Label)}.mkv";
        return Path.Combine(options.OutputDirectory, gameDirectory, dateDirectory, fileName);
    }

    private static string SanitizeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Recording" : sanitized;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _processGate.Dispose();
}
