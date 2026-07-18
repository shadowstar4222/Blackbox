using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class TimelineAssetServiceTests
{
    [Fact]
    public async Task GetOrCreateAsync_generates_thumbnail_and_waveform_once_then_uses_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.mkv");
        await File.WriteAllTextAsync(sourcePath, "source");
        try
        {
            var segment = TestSegments.Create(sourcePath, duration: TimeSpan.FromSeconds(10));
            var session = new RecordingSession(
                segment.SessionId,
                segment.StartTime,
                segment.EndTime,
                "Recording",
                [segment],
                false,
                false);
            var registry = new SegmentUsageRegistry();
            var runner = new AssetWritingRunner(registry, segment.Id);
            var provisioner = new FixedProvisioner(root);
            using var service = new TimelineAssetService(
                provisioner,
                runner,
                registry,
                new FfmpegOptions
                {
                    RootDirectory = root,
                    WorkDirectory = Path.Combine(root, "work"),
                    TimelineCacheDirectory = Path.Combine(root, "cache")
                },
                NullLogger<TimelineAssetService>.Instance);

            var generated = await service.GetOrCreateAsync(session);
            var cached = await service.GetOrCreateAsync(session);

            Assert.False(generated.LoadedFromCache);
            Assert.True(cached.LoadedFromCache);
            Assert.Single(generated.Thumbnails);
            Assert.Equal(480, generated.Waveform.Count);
            Assert.True(runner.SawLease);
            Assert.Equal(2, runner.Calls);
            Assert.Equal(1, provisioner.Calls);
            Assert.False(registry.IsInUse(segment.Id));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TryGetCachedAsync_does_not_provision_or_generate_when_cache_is_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.mkv");
        await File.WriteAllTextAsync(sourcePath, "source");
        try
        {
            var segment = TestSegments.Create(sourcePath, duration: TimeSpan.FromSeconds(10));
            var session = new RecordingSession(
                segment.SessionId,
                segment.StartTime,
                segment.EndTime,
                "Recording",
                [segment],
                false,
                false);
            var registry = new SegmentUsageRegistry();
            var runner = new AssetWritingRunner(registry, segment.Id);
            var provisioner = new FixedProvisioner(root);
            using var service = new TimelineAssetService(
                provisioner,
                runner,
                registry,
                new FfmpegOptions
                {
                    RootDirectory = root,
                    WorkDirectory = Path.Combine(root, "work"),
                    TimelineCacheDirectory = Path.Combine(root, "cache")
                },
                NullLogger<TimelineAssetService>.Instance);

            var missing = await service.TryGetCachedAsync(session);
            await service.GetOrCreateAsync(session);
            var cached = await service.TryGetCachedAsync(session);

            Assert.Null(missing);
            Assert.NotNull(cached);
            Assert.True(cached.LoadedFromCache);
            Assert.Equal(2, runner.Calls);
            Assert.Equal(1, provisioner.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetOrCreateAsync_serializes_publication_and_prunes_expired_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.mkv");
        await File.WriteAllTextAsync(sourcePath, "source");
        var expiredCache = Path.Combine(root, "cache", "expired-session", "expired-signature");
        Directory.CreateDirectory(expiredCache);
        await File.WriteAllTextAsync(Path.Combine(expiredCache, "old.jpg"), "old");
        Directory.SetLastWriteTimeUtc(expiredCache, DateTime.UtcNow.AddDays(-3));
        try
        {
            var segment = TestSegments.Create(sourcePath, duration: TimeSpan.FromSeconds(10));
            var session = new RecordingSession(
                segment.SessionId,
                segment.StartTime,
                segment.EndTime,
                "Recording",
                [segment],
                false,
                false);
            var registry = new SegmentUsageRegistry();
            var runner = new AssetWritingRunner(registry, segment.Id);
            using var service = new TimelineAssetService(
                new FixedProvisioner(root),
                runner,
                registry,
                new FfmpegOptions
                {
                    RootDirectory = root,
                    WorkDirectory = Path.Combine(root, "work"),
                    TimelineCacheDirectory = Path.Combine(root, "cache"),
                    TimelineCacheMaximumAge = TimeSpan.FromDays(1)
                },
                NullLogger<TimelineAssetService>.Instance);

            var results = await Task.WhenAll(
                service.GetOrCreateAsync(session),
                service.GetOrCreateAsync(session));

            Assert.Equal(2, runner.Calls);
            Assert.Single(results, result => !result.LoadedFromCache);
            Assert.Single(results, result => result.LoadedFromCache);
            Assert.False(Directory.Exists(expiredCache));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FixedProvisioner(string root) : IFfmpegProvisioner
    {
        public int Calls { get; private set; }

        public Task<FfmpegInstallation> EnsureInstalledAsync(
            IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new FfmpegInstallation(root, "ffmpeg", "ffprobe", "ffplay"));
        }
    }

    private sealed class AssetWritingRunner(ISegmentUsageRegistry registry, Guid segmentId) : IFfmpegCommandRunner
    {
        public int Calls { get; private set; }
        public bool SawLease { get; private set; }

        public async Task RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan expectedDuration,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            SawLease |= registry.IsInUse(segmentId);
            var output = arguments[^1];
            if (output.Contains("%04d", StringComparison.Ordinal))
            {
                await File.WriteAllBytesAsync(output.Replace("%04d", "0000"), [1, 2, 3], cancellationToken);
            }
            else
            {
                var samples = new short[] { 0, 8192, 16384, short.MaxValue };
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                await File.WriteAllBytesAsync(output, bytes, cancellationToken);
            }

            progress?.Report(100);
        }
    }
}
