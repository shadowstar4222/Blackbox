using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class SessionExportServiceTests
{
    [Fact]
    public async Task ExportAsync_writes_one_file_and_releases_source_segment_leases()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.mkv");
        var outputPath = Path.Combine(root, "export.mkv");
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
            var runner = new WritingCommandRunner(registry, segment.Id);
            var service = new SessionExportService(
                new FixedProvisioner(root),
                runner,
                registry,
                new FfmpegOptions { RootDirectory = root, WorkDirectory = Path.Combine(root, "work") },
                NullLogger<SessionExportService>.Instance);

            var result = await service.ExportAsync(new SessionExportRequest(
                session,
                TimeSpan.Zero,
                session.Duration,
                outputPath));

            Assert.True(File.Exists(outputPath));
            Assert.True(result.UsedStreamCopy);
            Assert.False(registry.IsInUse(segment.Id));
            Assert.True(runner.SawLease);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExportAsync_removes_partial_output_and_releases_leases_when_canceled()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.mkv");
        var outputPath = Path.Combine(root, "export.mkv");
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
            var service = new SessionExportService(
                new FixedProvisioner(root),
                new CancelingCommandRunner(registry, segment.Id),
                registry,
                new FfmpegOptions { RootDirectory = root, WorkDirectory = Path.Combine(root, "work") },
                NullLogger<SessionExportService>.Instance);

            await Assert.ThrowsAsync<OperationCanceledException>(() => service.ExportAsync(
                new SessionExportRequest(session, TimeSpan.Zero, session.Duration, outputPath)));

            Assert.False(File.Exists(outputPath));
            Assert.False(registry.IsInUse(segment.Id));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial-*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FixedProvisioner(string root) : IFfmpegProvisioner
    {
        public Task<FfmpegInstallation> EnsureInstalledAsync(
            IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfmpegInstallation(root, "ffmpeg", "ffprobe", "ffplay"));
    }

    private sealed class WritingCommandRunner(ISegmentUsageRegistry registry, Guid segmentId) : IFfmpegCommandRunner
    {
        public bool SawLease { get; private set; }

        public async Task RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan expectedDuration,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            SawLease = registry.IsInUse(segmentId);
            await File.WriteAllTextAsync(arguments[^1], "exported", cancellationToken);
            progress?.Report(100);
        }
    }

    private sealed class CancelingCommandRunner(ISegmentUsageRegistry registry, Guid segmentId) : IFfmpegCommandRunner
    {
        public async Task RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan expectedDuration,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Assert.True(registry.IsInUse(segmentId));
            await File.WriteAllTextAsync(arguments[^1], "partial", cancellationToken);
            throw new OperationCanceledException();
        }
    }
}
