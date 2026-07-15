using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class TimelineAssetService(
    IFfmpegProvisioner ffmpegProvisioner,
    IFfmpegCommandRunner commandRunner,
    ISegmentUsageRegistry usageRegistry,
    FfmpegOptions options,
    ILogger<TimelineAssetService> logger)
{
    private const int WaveformBucketCount = 480;

    public async Task<TimelineAssets> GetOrCreateAsync(
        RecordingSession session,
        IProgress<RecordingLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        var installation = await ffmpegProvisioner.EnsureInstalledAsync(cancellationToken: cancellationToken);
        var sessionCache = Path.Combine(GetCacheRoot(), session.Id.ToString("N"));
        var signature = CreateSignature(session);
        var cacheDirectory = Path.Combine(sessionCache, signature);
        var manifestPath = Path.Combine(cacheDirectory, "timeline.json");
        var cached = await TryLoadAsync(manifestPath, cacheDirectory, cancellationToken);
        if (cached is not null)
        {
            progress?.Report(new RecordingLibraryProgress("Timeline preview is ready.", 100));
            return cached with { LoadedFromCache = true };
        }

        Directory.CreateDirectory(sessionCache);
        Directory.CreateDirectory(options.WorkDirectory);
        var stagingDirectory = Path.Combine(sessionCache, $".partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        using var lease = usageRegistry.Acquire(session.Segments.Select(static segment => segment.Id).ToArray());
        var concatPath = FfmpegConcatFile.Create(options.WorkDirectory, session.Segments);
        try
        {
            progress?.Report(new RecordingLibraryProgress("Generating timeline thumbnails...", 0));
            var thumbnailPattern = Path.Combine(stagingDirectory, "thumb-%04d.jpg");
            await commandRunner.RunAsync(
                installation.FfmpegPath,
                TimelineAssetArgumentBuilder.BuildThumbnails(concatPath, thumbnailPattern, session.Duration),
                session.Duration,
                new Progress<double>(percent => progress?.Report(new RecordingLibraryProgress(
                    "Generating timeline thumbnails...",
                    percent * 0.55))),
                cancellationToken);

            var thumbnailFiles = Directory
                .EnumerateFiles(stagingDirectory, "thumb-*.jpg", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (thumbnailFiles.Length == 0)
            {
                throw new InvalidDataException("FFmpeg did not produce timeline thumbnails.");
            }

            progress?.Report(new RecordingLibraryProgress("Generating audio waveform...", 56));
            var pcmPath = Path.Combine(stagingDirectory, "waveform.pcm");
            await commandRunner.RunAsync(
                installation.FfmpegPath,
                TimelineAssetArgumentBuilder.BuildWaveform(concatPath, pcmPath),
                session.Duration,
                new Progress<double>(percent => progress?.Report(new RecordingLibraryProgress(
                    "Generating audio waveform...",
                    56 + percent * 0.4))),
                cancellationToken);
            var waveform = WaveformSampler.Sample(
                await File.ReadAllBytesAsync(pcmPath, cancellationToken),
                WaveformBucketCount);
            File.Delete(pcmPath);

            var interval = session.Duration.TotalSeconds / thumbnailFiles.Length;
            var manifest = new TimelineManifest(
                thumbnailFiles.Select((path, index) => new ThumbnailManifest(
                    index * interval,
                    Path.GetFileName(path))).ToArray(),
                waveform.ToArray());
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, "timeline.json"),
                JsonSerializer.Serialize(manifest),
                cancellationToken);

            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, true);
            }

            Directory.Move(stagingDirectory, cacheDirectory);
            var result = ToAssets(manifest, cacheDirectory, false);
            progress?.Report(new RecordingLibraryProgress("Timeline preview is ready.", 100));
            logger.LogInformation(
                "Generated timeline assets for recording session {RecordingSessionId}.",
                session.Id);
            return result;
        }
        finally
        {
            TryDeleteFile(concatPath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private string GetCacheRoot()
    {
        return options.TimelineCacheDirectory ?? Path.Combine(
            Directory.GetParent(options.RootDirectory)?.FullName ?? options.RootDirectory,
            "timeline-cache");
    }

    private static async Task<TimelineAssets?> TryLoadAsync(
        string manifestPath,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<TimelineManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken));
            return manifest is not null && manifest.Thumbnails.All(thumbnail =>
                    File.Exists(Path.Combine(cacheDirectory, thumbnail.FileName)))
                ? ToAssets(manifest, cacheDirectory, true)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimelineAssets ToAssets(
        TimelineManifest manifest,
        string cacheDirectory,
        bool loadedFromCache)
    {
        return new TimelineAssets(
            manifest.Thumbnails.Select(thumbnail => new TimelineThumbnail(
                TimeSpan.FromSeconds(thumbnail.OffsetSeconds),
                Path.Combine(cacheDirectory, thumbnail.FileName))).ToArray(),
            manifest.Waveform,
            loadedFromCache);
    }

    private static string CreateSignature(RecordingSession session)
    {
        var value = new StringBuilder("timeline-v1");
        foreach (var segment in session.Segments.OrderBy(static segment => segment.StartTime))
        {
            var file = new FileInfo(segment.FilePath);
            value.Append('|')
                .Append(Path.GetFullPath(segment.FilePath).ToUpperInvariant())
                .Append('|').Append(segment.FileSizeBytes)
                .Append('|').Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())))[..20];
    }

    private static void ValidateSession(RecordingSession session)
    {
        if (session.HasMissingSegments || session.HasGaps || session.HasDamagedSegments)
        {
            throw new InvalidOperationException("Timeline previews require a continuous session with readable source media.");
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed record TimelineManifest(
        IReadOnlyList<ThumbnailManifest> Thumbnails,
        IReadOnlyList<double> Waveform);

    private sealed record ThumbnailManifest(double OffsetSeconds, string FileName);
}
