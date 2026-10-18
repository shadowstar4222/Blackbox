using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class RecordingLibraryService(
    ISegmentRepository repository,
    IMediaProbe mediaProbe,
    IFfmpegProvisioner ffmpegProvisioner,
    RecordingSessionCatalog catalog,
    RecordingSettings recordingSettings,
    ILogger<RecordingLibraryService> logger,
    IClock? clock = null,
    IClipRepository? clipRepository = null)
{
    private static readonly TimeSpan ContinuationTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumOverlap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingMarkerRetention = TimeSpan.FromDays(7);
    private readonly IClock _clock = clock ?? new SystemClock();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, RecordingFileState> _observedFiles = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<RecordingSession>> RefreshAsync(
        IProgress<RecordingLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshCoreAsync(progress, cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<RecordingSession>> RefreshCoreAsync(
        IProgress<RecordingLibraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        recordingSettings.Validate();
        await repository.InitializeAsync(cancellationToken);
        Directory.CreateDirectory(recordingSettings.RecordingLocation);
        var mediaFiles = RecordingFileState.Enumerate(recordingSettings.RecordingLocation)
            .Select(static path => new FileInfo(Path.GetFullPath(path)))
            .Where(static file => file.Exists && file.Length > 0 &&
                DateTimeOffset.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromSeconds(1))
            .OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var existingSegments = (await repository.GetAllAsync(cancellationToken))
            .ToDictionary(
                static segment => Path.GetFullPath(segment.FilePath),
                StringComparer.OrdinalIgnoreCase);
        var provisioned = false;
        var candidates = new List<MediaCandidate>(mediaFiles.Length);
        for (var index = 0; index < mediaFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = mediaFiles[index];
            progress?.Report(new RecordingLibraryProgress(
                $"Indexing {file.Name}...",
                45 + (index + 1) * 45d / mediaFiles.Length));
            existingSegments.TryGetValue(file.FullName, out var existing);
            var snapshot = new RecordingFileState(file.FullName, file.Length, file.LastWriteTimeUtc);
            var observed = _observedFiles.TryGetValue(file.FullName, out var previousSnapshot);
            if (existing is not null &&
                existing.FileSizeBytes == file.Length &&
                (observed ? previousSnapshot == snapshot : !existing.IsDamaged))
            {
                candidates.Add(MediaCandidate.FromExisting(
                    file,
                    existing,
                    RecordingDirectoryLayout.GetApplicationName(
                        recordingSettings.RecordingLocation,
                        file.FullName)));
                _observedFiles[file.FullName] = snapshot;
                continue;
            }

            using var readLease = RecordingFileState.TryOpenReadLease(file.FullName);
            if (readLease is null || RecordingFileState.Capture(file.FullName) != snapshot)
            {
                continue;
            }

            if (!provisioned)
            {
                await ffmpegProvisioner.EnsureInstalledAsync(cancellationToken: cancellationToken);
                provisioned = true;
            }

            try
            {
                var probe = await mediaProbe.ProbeAsync(file.FullName, cancellationToken);
                candidates.Add(MediaCandidate.FromProbe(
                    file,
                    existing,
                    probe,
                    RecordingDirectoryLayout.GetApplicationName(
                        recordingSettings.RecordingLocation,
                        file.FullName)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                candidates.Add(MediaCandidate.FromDamaged(
                    file,
                    existing,
                    ex.Message,
                    RecordingDirectoryLayout.GetApplicationName(
                        recordingSettings.RecordingLocation,
                        file.FullName)));
                logger.LogWarning(
                    ex,
                    "Recording {RecordingPath} could not be read and was marked as damaged.",
                    file.FullName);
            }

            _observedFiles[file.FullName] = snapshot;
        }

        SetNativeStartTimes(candidates);
        var groups = GroupCandidates(candidates.OrderBy(static candidate => candidate.StartTime).ToArray());
        foreach (var group in groups)
        {
            var sessionId = group.Select(static candidate => candidate.Existing?.SessionId)
                .FirstOrDefault(static id => id is not null)
                ?? group[0].NativeFile?.RecordingId
                ?? CreateDeterministicId(group[0].File.FullName);
            foreach (var candidate in group)
            {
                var segment = candidate.ToSegment(candidate.Existing?.SessionId ?? sessionId);
                if (segment != candidate.Existing)
                {
                    await repository.UpsertAsync(segment, cancellationToken);
                }
            }
        }

        var presentPaths = mediaFiles.Select(static file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _observedFiles.Keys.Where(path => !presentPaths.Contains(path)).ToArray())
        {
            _observedFiles.Remove(path);
        }

        var sessions = await catalog.GetSessionsAsync(cancellationToken);
        if (await ResolvePendingMarkersAsync(sessions, cancellationToken))
        {
            sessions = await catalog.GetSessionsAsync(cancellationToken);
        }
        progress?.Report(new RecordingLibraryProgress(
            $"Found {sessions.Count} recording session(s).",
            100));
        logger.LogInformation(
            "Refreshed recording library with {RecordingCount} file(s) in {SessionCount} session(s).",
            mediaFiles.Length,
            sessions.Count);
        return sessions;
    }

    public async Task<TimelineMarker> AddMarkerAsync(
        RecordingSession session,
        TimeSpan offset,
        string? label = null,
        TimeSpan contextBefore = default,
        TimeSpan contextAfter = default,
        CancellationToken cancellationToken = default)
    {
        if (offset < TimeSpan.Zero || offset > session.Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The marker is outside this recording.");
        }

        var marker = new TimelineMarker(
            Guid.NewGuid(),
            session.Id,
            offset,
            string.IsNullOrWhiteSpace(label) ? $"Marker {FormatOffset(offset)}" : label.Trim(),
            _clock.UtcNow,
            contextBefore,
            contextAfter,
            TimelineMarkerOrigin.Manual);
        marker.Validate();
        await repository.AddMarkerAsync(marker, cancellationToken);
        logger.LogInformation(
            "Added marker {MarkerId} to recording session {RecordingSessionId} at {MarkerOffset}.",
            marker.Id,
            session.Id,
            marker.Offset);
        return marker;
    }

    public async Task DeleteMarkerAsync(
        Guid markerId,
        CancellationToken cancellationToken = default)
    {
        if (clipRepository is not null)
        {
            await clipRepository.DeleteMarkerDependenciesAsync(markerId, cancellationToken);
        }

        await repository.DeleteMarkerAsync(markerId, cancellationToken);
    }

    private async Task<bool> ResolvePendingMarkersAsync(
        IReadOnlyList<RecordingSession> sessions,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var pendingMarkers = (await repository.GetMarkersAsync(cancellationToken))
            .Where(static marker => marker.SessionId == Guid.Empty)
            .ToArray();
        foreach (var marker in pendingMarkers)
        {
            var session = sessions
                .Where(candidate =>
                    marker.CreatedAt >= candidate.StartTime - ContinuationTolerance &&
                    marker.CreatedAt <= candidate.EndTime + ContinuationTolerance)
                .OrderBy(candidate => GetDistance(candidate, marker.CreatedAt))
                .FirstOrDefault();
            if (session is not null)
            {
                var offset = RecordingTimeline.ToOffset(session, marker.CreatedAt);
                await repository.UpdateMarkerPlacementAsync(
                    marker.Id,
                    session.Id,
                    offset,
                    cancellationToken);
                logger.LogInformation(
                    "Placed live marker {MarkerId} in session {SessionId} at {MarkerOffset}.",
                    marker.Id,
                    session.Id,
                    offset);
                changed = true;
            }
            else if (_clock.UtcNow - marker.CreatedAt > PendingMarkerRetention)
            {
                await DeleteMarkerAsync(marker.Id, cancellationToken);
                logger.LogWarning(
                    "Removed stale live marker {MarkerId} because no recording session contained it.",
                    marker.Id);
                changed = true;
            }
        }

        return changed;
    }

    private static TimeSpan GetDistance(
        RecordingSession session,
        DateTimeOffset timestamp)
    {
        if (timestamp < session.StartTime)
        {
            return session.StartTime - timestamp;
        }

        return timestamp > session.EndTime
            ? timestamp - session.EndTime
            : TimeSpan.Zero;
    }

    public async Task<ProtectedTimelineRange> ProtectRangeAsync(
        RecordingSession session,
        TimeSpan rangeStart,
        TimeSpan rangeEnd,
        CancellationToken cancellationToken = default)
    {
        if (rangeStart < TimeSpan.Zero || rangeEnd <= rangeStart || rangeEnd > session.Duration)
        {
            throw new InvalidOperationException("The protected selection is outside this recording.");
        }

        var startTime = RecordingTimeline.ToTimestamp(session, rangeStart);
        var endTime = RecordingTimeline.ToTimestamp(session, rangeEnd);
        await repository.MarkProtectedRangeAsync(startTime, endTime, cancellationToken);
        return new ProtectedTimelineRange(Guid.Empty, startTime, endTime, DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<List<MediaCandidate>> GroupCandidates(IReadOnlyList<MediaCandidate> candidates)
    {
        var groups = new List<List<MediaCandidate>>();
        var nativeGroups = new Dictionary<string, List<MediaCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate.NativeFile is { } native)
            {
                if (!nativeGroups.TryGetValue(native.PrefixPath, out var nativeGroup))
                {
                    nativeGroup = [];
                    nativeGroups.Add(native.PrefixPath, nativeGroup);
                    groups.Add(nativeGroup);
                }

                nativeGroup.Add(candidate);
                continue;
            }

            if (groups.Count == 0)
            {
                groups.Add([candidate]);
                continue;
            }

            var current = groups[^1];
            var previous = current[^1];
            var gap = candidate.StartTime - previous.EndTime;
            if (previous.NativeFile is not null || gap > ContinuationTolerance ||
                gap < -MaximumOverlap || !AreCompatible(previous, candidate))
            {
                groups.Add([candidate]);
            }
            else
            {
                current.Add(candidate);
            }
        }

        return groups;
    }

    private static bool AreCompatible(MediaCandidate left, MediaCandidate right) =>
        left.File.Extension.Equals(right.File.Extension, StringComparison.OrdinalIgnoreCase) &&
        left.Width == right.Width &&
        left.Height == right.Height &&
        left.GameTitle.Equals(right.GameTitle, StringComparison.OrdinalIgnoreCase) &&
        left.AudioTrackTitles.Count == right.AudioTrackTitles.Count &&
        left.VideoCodec.Equals(right.VideoCodec, StringComparison.OrdinalIgnoreCase);

    private void SetNativeStartTimes(List<MediaCandidate> candidates)
    {
        var candidateIndexes = candidates.Select(static (candidate, index) => (candidate.File.FullName, index))
            .ToDictionary(static item => item.FullName, static item => item.index, StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.Where(static candidate => candidate.NativeFile is not null)
                     .GroupBy(static candidate => candidate.NativeFile!.PrefixPath, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(static candidate => candidate.NativeFile!.SegmentIndex).ToArray();
            var offsets = ordered.Any(static candidate => candidate.Existing is null)
                ? ordered[0].NativeFile!.ReadSegmentRanges()
                : new Dictionary<int, (TimeSpan Start, TimeSpan Duration)>();
            MediaCandidate? previous = null;
            foreach (var candidate in ordered)
            {
                var current = candidate;
                var native = candidate.NativeFile!;
                if (candidate.Existing is null)
                {
                    var hasRange = offsets.TryGetValue(native.SegmentIndex, out var range);
                    var start = hasRange
                        ? native.RecordingStart + range.Start
                        : previous?.NativeFile?.SegmentIndex == native.SegmentIndex - 1
                            ? previous.EndTime
                            : native.RecordingStart + TimeSpan.FromMinutes(
                                (double)native.SegmentIndex * recordingSettings.SegmentDurationMinutes);
                    // AAC packets can overhang the video boundary by a few milliseconds.
                    // Use the closed-segment timeline to prevent cumulative review/marker drift.
                    current = candidate with { StartTime = start, Duration = hasRange && range.Duration < candidate.Duration ? range.Duration : candidate.Duration };
                    candidates[candidateIndexes[candidate.File.FullName]] = current;
                }

                previous = current;
            }
        }
    }

    private static Guid CreateDeterministicId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string FormatOffset(TimeSpan offset) =>
        offset.TotalHours >= 1 ? offset.ToString(@"h\:mm\:ss") : offset.ToString(@"m\:ss");

    private sealed record MediaCandidate(
        FileInfo File,
        Guid SegmentId,
        DateTimeOffset StartTime,
        TimeSpan Duration,
        string GameExecutable,
        string GameTitle,
        string VideoCodec,
        string PixelFormat,
        int Width,
        int Height,
        decimal FrameRate,
        bool IsHdr,
        bool IsProtected,
        IReadOnlyList<string> AudioTrackTitles,
        bool IsDamaged,
        string? DamageDetail)
    {
        public DateTimeOffset EndTime => StartTime + Duration;
        public RecordingSegment? Existing { get; init; }
        public bool WasProbed { get; init; }
        public NativeRecordingFile? NativeFile { get; } = NativeRecordingFile.TryParse(File.FullName);

        public static MediaCandidate FromExisting(
            FileInfo file,
            RecordingSegment segment,
            string applicationName) => new(
            file,
            segment.Id,
            segment.StartTime,
            segment.EndTime - segment.StartTime,
            segment.GameExecutable,
            applicationName == "Recording" ? segment.GameTitle : applicationName,
            segment.Encoder,
            segment.VideoFormat,
            segment.Width,
            segment.Height,
            segment.FrameRate,
            segment.IsHdr,
            segment.IsProtected,
            ParseAudioTitles(segment.AudioTrackLayout),
            segment.IsDamaged,
            segment.DamageDetail) { Existing = segment };

        public static MediaCandidate FromProbe(
            FileInfo file,
            RecordingSegment? existing,
            MediaFileProbeResult probe,
            string applicationName) => new(
            file,
            existing?.Id ?? Guid.NewGuid(),
            existing?.StartTime ?? ParseStartTime(file),
            probe.Duration,
            existing?.GameExecutable ?? string.Empty,
            applicationName == "Recording"
                ? existing?.GameTitle ?? "Recording"
                : applicationName,
            probe.VideoCodec,
            probe.PixelFormat,
            probe.Width,
            probe.Height,
            probe.FrameRate,
            probe.IsHdr,
            existing?.IsProtected ?? false,
            probe.AudioTrackTitles,
            false,
            null) { Existing = existing, WasProbed = true };

        public static MediaCandidate FromDamaged(
            FileInfo file,
            RecordingSegment? existing,
            string detail,
            string applicationName)
        {
            if (existing is not null)
            {
                return FromExisting(file, existing, applicationName) with
                {
                    IsDamaged = true,
                    DamageDetail = detail
                };
            }

            return new MediaCandidate(
                file,
                Guid.NewGuid(),
                ParseStartTime(file),
                TimeSpan.FromSeconds(1),
                string.Empty,
                applicationName == "Recording" ? "Damaged recording" : applicationName,
                "unknown",
                "unknown",
                0,
                0,
                0,
                false,
                false,
                [],
                true,
                detail);
        }

        public RecordingSegment ToSegment(Guid sessionId) => Existing is not null && !WasProbed
            ? Existing with
            {
                GameTitle = GameTitle,
                FileSizeBytes = File.Length,
                IsDamaged = IsDamaged,
                DamageDetail = DamageDetail
            }
            : new(
            SegmentId,
            sessionId,
            StartTime,
            EndTime,
            GameExecutable,
            GameTitle,
            PixelFormat,
            string.Join(';', AudioTrackTitles.Select((title, index) => $"{index + 1}:{title}")),
            VideoCodec,
            Width,
            Height,
            FrameRate,
            IsHdr,
            IsProtected,
            File.FullName,
            File.Length,
            IsDamaged,
            DamageDetail);

        private static DateTimeOffset ParseStartTime(FileInfo file)
        {
            if (DateTime.TryParseExact(
                Path.GetFileNameWithoutExtension(file.Name),
                "yyyy-MM-dd HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(localTime, DateTimeKind.Local));
            }

            return file.CreationTime;
        }

        private static IReadOnlyList<string> ParseAudioTitles(string layout)
        {
            return layout
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => value.Contains(':') ? value[(value.IndexOf(':') + 1)..] : value)
                .Select(static value => value.Trim().ToLowerInvariant() switch
                {
                    "full_mix" => "Full listening mix",
                    "game" => "Game audio",
                    "voice" => "Voice chat",
                    "raw_mic" => "Raw microphone",
                    "processed_mic" => "Processed microphone",
                    _ => value.Trim()
                })
                .ToArray();
        }
    }
}
