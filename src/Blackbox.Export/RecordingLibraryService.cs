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
    ILogger<RecordingLibraryService> logger)
{
    private static readonly TimeSpan ContinuationTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumOverlap = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<RecordingSession>> RefreshAsync(
        IProgress<RecordingLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        recordingSettings.Validate();
        await repository.InitializeAsync(cancellationToken);
        Directory.CreateDirectory(recordingSettings.RecordingLocation);
        var mediaFiles = Directory
            .EnumerateFiles(recordingSettings.RecordingLocation, "*", SearchOption.TopDirectoryOnly)
            .Where(IsRecordingFile)
            .Select(static path => new FileInfo(Path.GetFullPath(path)))
            .Where(static file => file.Exists && file.Length > 0 &&
                DateTimeOffset.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromSeconds(1))
            .OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mediaFiles.Length == 0)
        {
            progress?.Report(new RecordingLibraryProgress("No completed recordings found.", 100));
            return [];
        }

        var existingSegments = (await repository.GetAllAsync(cancellationToken))
            .ToDictionary(
                static segment => Path.GetFullPath(segment.FilePath),
                StringComparer.OrdinalIgnoreCase);
        var provisionProgress = new Progress<FfmpegProvisionProgress>(update =>
            progress?.Report(new RecordingLibraryProgress(
                update.Message,
                update.Percent is null ? null : update.Percent * 0.45)));
        await ffmpegProvisioner.EnsureInstalledAsync(provisionProgress, cancellationToken);

        var candidates = new List<MediaCandidate>(mediaFiles.Length);
        for (var index = 0; index < mediaFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = mediaFiles[index];
            progress?.Report(new RecordingLibraryProgress(
                $"Indexing {file.Name}...",
                45 + (index + 1) * 45d / mediaFiles.Length));
            existingSegments.TryGetValue(file.FullName, out var existing);
            try
            {
                var probe = await mediaProbe.ProbeAsync(file.FullName, cancellationToken);
                candidates.Add(MediaCandidate.FromProbe(file, existing, probe));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                candidates.Add(MediaCandidate.FromDamaged(file, existing, ex.Message));
                logger.LogWarning(
                    ex,
                    "Recording {RecordingPath} could not be read and was marked as damaged.",
                    file.FullName);
            }
        }

        var groups = GroupCandidates(candidates.OrderBy(static candidate => candidate.StartTime).ToArray());
        foreach (var group in groups)
        {
            var sessionId = CreateDeterministicId(group[0].File.FullName);
            foreach (var candidate in group)
            {
                await repository.UpsertAsync(candidate.ToSegment(sessionId), cancellationToken);
            }
        }

        var sessions = await catalog.GetSessionsAsync(cancellationToken);
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
            DateTimeOffset.UtcNow);
        await repository.AddMarkerAsync(marker, cancellationToken);
        logger.LogInformation(
            "Added marker {MarkerId} to recording session {RecordingSessionId} at {MarkerOffset}.",
            marker.Id,
            session.Id,
            marker.Offset);
        return marker;
    }

    public Task DeleteMarkerAsync(Guid markerId, CancellationToken cancellationToken = default) =>
        repository.DeleteMarkerAsync(markerId, cancellationToken);

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
        foreach (var candidate in candidates)
        {
            if (groups.Count == 0)
            {
                groups.Add([candidate]);
                continue;
            }

            var current = groups[^1];
            var previous = current[^1];
            var gap = candidate.StartTime - previous.EndTime;
            if (gap > ContinuationTolerance || gap < -MaximumOverlap || !AreCompatible(previous, candidate))
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
        left.AudioTrackTitles.Count == right.AudioTrackTitles.Count &&
        left.VideoCodec.Equals(right.VideoCodec, StringComparison.OrdinalIgnoreCase);

    private static bool IsRecordingFile(string path)
    {
        if (path.Contains(".partial.", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".active.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
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

        public static MediaCandidate FromExisting(FileInfo file, RecordingSegment segment) => new(
            file,
            segment.Id,
            segment.StartTime,
            segment.EndTime - segment.StartTime,
            segment.GameExecutable,
            segment.GameTitle,
            segment.Encoder,
            segment.VideoFormat,
            segment.Width,
            segment.Height,
            segment.FrameRate,
            segment.IsHdr,
            segment.IsProtected,
            ParseAudioTitles(segment.AudioTrackLayout),
            segment.IsDamaged,
            segment.DamageDetail);

        public static MediaCandidate FromProbe(
            FileInfo file,
            RecordingSegment? existing,
            MediaFileProbeResult probe) => new(
            file,
            existing?.Id ?? Guid.NewGuid(),
            ParseStartTime(file),
            probe.Duration,
            existing?.GameExecutable ?? string.Empty,
            existing?.GameTitle ?? "Recording",
            probe.VideoCodec,
            probe.PixelFormat,
            probe.Width,
            probe.Height,
            probe.FrameRate,
            probe.IsHdr,
            existing?.IsProtected ?? false,
            probe.AudioTrackTitles,
            false,
            null);

        public static MediaCandidate FromDamaged(
            FileInfo file,
            RecordingSegment? existing,
            string detail)
        {
            if (existing is not null)
            {
                return FromExisting(file, existing) with
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
                "Damaged recording",
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

        public RecordingSegment ToSegment(Guid sessionId) => new(
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
