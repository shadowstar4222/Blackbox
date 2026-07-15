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
        progress?.Report(new ExportProgress(ExportStage.Preparing, "Preparing the recording export...", 0));
        var provisionProgress = new Progress<FfmpegProvisionProgress>(update =>
            progress?.Report(new ExportProgress(
                ExportStage.Preparing,
                update.Message,
                update.Percent is null ? null : update.Percent * 0.1)));
        var installation = await ffmpegProvisioner.EnsureInstalledAsync(provisionProgress, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))
            ?? throw new InvalidOperationException("The export destination has no parent directory."));
        Directory.CreateDirectory(options.WorkDirectory);

        using var usageLease = usageRegistry.Acquire(request.Session.Segments.Select(static segment => segment.Id).ToArray());
        var concatPath = FfmpegConcatFile.Create(options.WorkDirectory, request.Session.Segments);
        var extension = Path.GetExtension(request.DestinationPath);
        var temporaryOutputPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))!,
            $"{Path.GetFileNameWithoutExtension(request.DestinationPath)}.partial-{Guid.NewGuid():N}{extension}");
        try
        {
            var arguments = FfmpegExportArgumentBuilder.Build(
                request,
                concatPath,
                temporaryOutputPath,
                out var usesStreamCopy);
            var expectedDuration = request.RangeEnd - request.RangeStart;
            var commandProgress = new Progress<double>(percent =>
                progress?.Report(new ExportProgress(
                    ExportStage.Exporting,
                    usesStreamCopy ? "Joining recording segments..." : "Rendering the selected range...",
                    10 + percent * 0.85)));
            await commandRunner.RunAsync(
                installation.FfmpegPath,
                arguments,
                expectedDuration,
                commandProgress,
                cancellationToken);

            progress?.Report(new ExportProgress(ExportStage.Finalizing, "Finalizing the exported video...", 97));
            var output = new FileInfo(temporaryOutputPath);
            if (!output.Exists || output.Length == 0)
            {
                throw new InvalidOperationException("FFmpeg did not produce the exported video.");
            }

            File.Move(temporaryOutputPath, request.DestinationPath, true);
            output = new FileInfo(request.DestinationPath);
            progress?.Report(new ExportProgress(ExportStage.Complete, "Export complete.", 100));
            logger.LogInformation(
                "Exported recording session {RecordingSessionId} to {ExportPath}. StreamCopy={UsedStreamCopy}.",
                request.Session.Id,
                request.DestinationPath,
                usesStreamCopy);
            return new SessionExportResult(
                request.DestinationPath,
                expectedDuration,
                output.Length,
                usesStreamCopy);
        }
        finally
        {
            TryDeleteFile(concatPath);
            TryDeleteFile(temporaryOutputPath);
        }
    }

    private static void ValidateSession(RecordingSession session)
    {
        if (session.HasMissingSegments)
        {
            throw new InvalidOperationException("This recording has a missing segment. Blackbox will not silently skip it.");
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
