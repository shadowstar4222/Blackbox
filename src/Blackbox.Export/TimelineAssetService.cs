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
    ILogger<TimelineAssetService> logger) : IDisposable
{
    private const int WaveformBucketCount = 480;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public async Task<TimelineAssets> GetOrCreateAsync(
        RecordingSession session,
        IProgress<RecordingLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            return await GetOrCreateCoreAsync(session, progress, cancellationToken);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async Task<TimelineAssets?> TryGetCachedAsync(
        RecordingSession session,
        CancellationToken cancellationToken = default)
    {
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            ValidateSession(session);
            ValidateCacheOptions();
            var sessionCache = Path.Combine(GetCacheRoot(), session.Id.ToString("N"));
            var signature = CreateSignature(session);
            var cacheDirectory = Path.Combine(sessionCache, signature);
            var cached = await TryLoadAsync(
                Path.Combine(cacheDirectory, "timeline.json"),
                cacheDirectory,
                cancellationToken);
            return cached is null ? null : cached with { LoadedFromCache = true };
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<TimelineAssets> GetOrCreateCoreAsync(
        RecordingSession session,
        IProgress<RecordingLibraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateSession(session);
        ValidateCacheOptions();
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

        var installation = await ffmpegProvisioner.EnsureInstalledAsync(cancellationToken: cancellationToken);
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
            PruneCache(cacheDirectory);
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

    private void ValidateCacheOptions()
    {
        if (options.TimelineCacheMaximumBytes < 1024 * 1024)
        {
            throw new InvalidOperationException("Timeline cache size must be at least 1 MB.");
        }

        if (options.TimelineCacheMaximumAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Timeline cache age must be greater than zero.");
        }
    }

    private void PruneCache(string currentCacheDirectory)
    {
        var cacheRoot = GetCacheRoot();
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var directories = Directory
                .EnumerateDirectories(cacheRoot, "*", SearchOption.TopDirectoryOnly)
                .SelectMany(static sessionDirectory =>
                    Directory.EnumerateDirectories(sessionDirectory, "*", SearchOption.TopDirectoryOnly))
                .Where(path =>
                    !path.Equals(currentCacheDirectory, StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(path).StartsWith(".partial-", StringComparison.OrdinalIgnoreCase))
                .Select(TryInspectCacheDirectory)
                .OfType<CacheDirectory>()
                .OrderBy(static directory => directory.LastWriteTimeUtc)
                .ToList();
            foreach (var expired in directories
                         .Where(directory => now - directory.LastWriteTimeUtc > options.TimelineCacheMaximumAge)
                         .ToArray())
            {
                if (TryDeleteCacheDirectory(expired.Path))
                {
                    directories.Remove(expired);
                }
            }

            var currentBytes = GetDirectorySize(currentCacheDirectory);
            var totalBytes = currentBytes + directories.Sum(static directory => directory.SizeBytes);
            foreach (var candidate in directories)
            {
                if (totalBytes <= options.TimelineCacheMaximumBytes)
                {
                    break;
                }

                if (TryDeleteCacheDirectory(candidate.Path))
                {
                    totalBytes -= candidate.SizeBytes;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prune the timeline preview cache.");
        }
    }

    private static CacheDirectory? TryInspectCacheDirectory(string path)
    {
        try
        {
            return new CacheDirectory(
                path,
                GetDirectorySize(path),
                Directory.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long size = 0;
        foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            size = checked(size + new FileInfo(filePath).Length);
        }

        return size;
    }

    private static bool TryDeleteCacheDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

    public void Dispose() => _cacheGate.Dispose();

    private sealed record TimelineManifest(
        IReadOnlyList<ThumbnailManifest> Thumbnails,
        IReadOnlyList<double> Waveform);

    private sealed record ThumbnailManifest(double OffsetSeconds, string FileName);
    private sealed record CacheDirectory(string Path, long SizeBytes, DateTime LastWriteTimeUtc);
}
