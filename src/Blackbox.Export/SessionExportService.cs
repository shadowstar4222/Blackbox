using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class SessionExportService(
    IFfmpegProvisioner ffmpegProvisioner,
    IFfmpegCommandRunner commandRunner,
    ISegmentUsageRegistry usageRegistry,
    FfmpegOptions options,
    ILogger<SessionExportService> logger)
{
    public async Task<SessionExportResult> ExportAsync(
        SessionExportRequest request,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        ValidateSession(request.Session);
        var configuredTracks = AudioTrackSelectionResolver.Resolve(request);
        ValidateTracks(request.Session, configuredTracks);
        var wavTracks = configuredTracks.Where(static track => track.ExportAsWav).ToArray();
        progress?.Report(new ExportProgress(ExportStage.Preparing, "Preparing the recording export...", 0));
        var provisionProgress = new Progress<FfmpegProvisionProgress>(update =>
            progress?.Report(new ExportProgress(
                ExportStage.Preparing,
                update.Message,
                update.Percent is null ? null : update.Percent * 0.1)));
        var installation = await ffmpegProvisioner.EnsureInstalledAsync(provisionProgress, cancellationToken);
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))
            ?? throw new InvalidOperationException("The export destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        Directory.CreateDirectory(options.WorkDirectory);

        using var usageLease = usageRegistry.Acquire(
            request.Session.Segments.Select(static segment => segment.Id).ToArray());
        var concatPath = FfmpegConcatFile.Create(options.WorkDirectory, request.Session.Segments);
        var temporaryPaths = new List<string>();
        var publishedAudioPaths = new List<string>();
        var extension = Path.GetExtension(request.DestinationPath);
        var temporaryVideoPath = CreateTemporaryPath(request.DestinationPath, extension);
        temporaryPaths.Add(temporaryVideoPath);
        try
        {
            var arguments = FfmpegExportArgumentBuilder.Build(
                request,
                concatPath,
                temporaryVideoPath,
                out var usesStreamCopy);
            var expectedDuration = request.RangeEnd - request.RangeStart;
            var videoProgressCeiling = wavTracks.Length == 0 ? 95d : 78d;
            var commandProgress = new Progress<double>(percent =>
                progress?.Report(new ExportProgress(
                    ExportStage.Exporting,
                    usesStreamCopy ? "Joining recording segments..." : "Rendering video and audio tracks...",
                    10 + percent / 100d * (videoProgressCeiling - 10))));
            await commandRunner.RunAsync(
                installation.FfmpegPath,
                arguments,
                expectedDuration,
                commandProgress,
                cancellationToken);
            EnsureOutput(temporaryVideoPath, "FFmpeg did not produce the exported video.");

            var wavOutputs = new List<(string Temporary, string Final)>();
            for (var index = 0; index < wavTracks.Length; index++)
            {
                var track = wavTracks[index];
                var finalPath = CreateAvailableWavPath(request.DestinationPath, track);
                var temporaryPath = CreateTemporaryPath(finalPath, ".wav");
                temporaryPaths.Add(temporaryPath);
                wavOutputs.Add((temporaryPath, finalPath));
                var trackStart = 78 + index * 17d / wavTracks.Length;
                var trackSpan = 17d / wavTracks.Length;
                progress?.Report(new ExportProgress(
                    ExportStage.Exporting,
                    $"Exporting {track.Name} WAV...",
                    trackStart));
                await commandRunner.RunAsync(
                    installation.FfmpegPath,
                    FfmpegAudioExportArgumentBuilder.Build(request, track, concatPath, temporaryPath),
                    expectedDuration,
                    new Progress<double>(percent => progress?.Report(new ExportProgress(
                        ExportStage.Exporting,
                        $"Exporting {track.Name} WAV...",
                        trackStart + percent / 100d * trackSpan))),
                    cancellationToken);
                EnsureOutput(temporaryPath, $"FFmpeg did not produce the {track.Name} WAV file.");
            }

            progress?.Report(new ExportProgress(ExportStage.Finalizing, "Finalizing exported files...", 97));
            foreach (var wavOutput in wavOutputs)
            {
                File.Move(wavOutput.Temporary, wavOutput.Final);
                publishedAudioPaths.Add(wavOutput.Final);
            }

            File.Move(temporaryVideoPath, request.DestinationPath, true);
            var output = new FileInfo(request.DestinationPath);
            progress?.Report(new ExportProgress(ExportStage.Complete, "Export complete.", 100));
            logger.LogInformation(
                "Exported recording session {RecordingSessionId} to {ExportPath}. StreamCopy={UsedStreamCopy}, WavFiles={WavFileCount}.",
                request.Session.Id,
                request.DestinationPath,
                usesStreamCopy,
                publishedAudioPaths.Count);
            return new SessionExportResult(
                request.DestinationPath,
                expectedDuration,
                output.Length,
                usesStreamCopy,
                publishedAudioPaths);
        }
        catch
        {
            foreach (var path in publishedAudioPaths)
            {
                TryDeleteFile(path);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(concatPath);
            foreach (var path in temporaryPaths)
            {
                TryDeleteFile(path);
            }
        }
    }

    private static void ValidateSession(RecordingSession session)
    {
        if (session.HasMissingSegments)
        {
            throw new InvalidOperationException("This recording has a missing segment. Blackbox will not silently skip it.");
        }

        if (session.HasDamagedSegments)
        {
            var detail = session.Segments.First(static segment => segment.IsDamaged).DamageDetail;
            throw new InvalidOperationException($"This recording contains damaged media: {detail}");
        }

        if (session.HasGaps)
        {
            throw new InvalidOperationException("This recording contains a timeline gap. Repair or split the session before exporting it.");
        }

        if (session.Segments.Any(static segment => !File.Exists(segment.FilePath)))
        {
            throw new InvalidOperationException("A source segment disappeared before export could begin.");
        }

        var first = session.Segments[0];
        if (session.Segments.Skip(1).Any(segment =>
            segment.Width != first.Width ||
            segment.Height != first.Height ||
            segment.FrameRate != first.FrameRate ||
            !segment.Encoder.Equals(first.Encoder, StringComparison.OrdinalIgnoreCase) ||
            !segment.AudioTrackLayout.Equals(first.AudioTrackLayout, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The recording changes media format between segments and cannot be joined without normalization.");
        }
    }

    private static void ValidateTracks(
        RecordingSession session,
        IReadOnlyList<AudioTrackExportSelection> tracks)
    {
        var availableCount = AudioTrackSelectionResolver.ParseLayout(
            session.Segments[0].AudioTrackLayout).Count;
        if (tracks.Any(track => track.StreamIndex >= availableCount))
        {
            throw new InvalidOperationException("An export audio selection refers to a track this recording does not contain.");
        }
    }

    private static string CreateTemporaryPath(string finalPath, string extension)
    {
        return Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(finalPath))!,
            $"{Path.GetFileNameWithoutExtension(finalPath)}.partial-{Guid.NewGuid():N}{extension}");
    }

    private static string CreateAvailableWavPath(
        string videoPath,
        AudioTrackExportSelection track)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(track.Name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        var directory = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        var baseName = $"{Path.GetFileNameWithoutExtension(videoPath)} - {track.StreamIndex + 1:00} {safeName}";
        var candidate = Path.Combine(directory, $"{baseName}.wav");
        for (var suffix = 2; File.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({suffix}).wav");
        }

        return candidate;
    }

    private static void EnsureOutput(string path, string errorMessage)
    {
        var output = new FileInfo(path);
        if (!output.Exists || output.Length == 0)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
