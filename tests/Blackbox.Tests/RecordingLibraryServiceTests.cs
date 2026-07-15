using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RecordingLibraryServiceTests
{
    [Fact]
    public async Task RefreshAsync_indexes_adjacent_files_as_one_continuous_session_and_reuses_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "2026-07-15 12-00-00.mkv");
        var secondPath = Path.Combine(root, "2026-07-15 12-02-00.mkv");
        await File.WriteAllTextAsync(firstPath, "one");
        await File.WriteAllTextAsync(secondPath, "two");
        File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(-2));
        File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddSeconds(-2));
        try
        {
            var repository = new InMemorySegmentRepository();
            var probe = new FixedMediaProbe();
            var provisioner = new FixedProvisioner(root);
            var catalog = new RecordingSessionCatalog(repository);
            var service = new RecordingLibraryService(
                repository,
                probe,
                provisioner,
                catalog,
                new RecordingSettings { RecordingLocation = root },
                NullLogger<RecordingLibraryService>.Instance);

            var firstRefresh = await service.RefreshAsync();
            var secondRefresh = await service.RefreshAsync();

            var session = Assert.Single(firstRefresh);
            Assert.Equal(2, session.Segments.Count);
            Assert.Equal(TimeSpan.FromMinutes(4), session.Duration);
            Assert.Single(secondRefresh);
            Assert.Equal(2, probe.Calls);
            Assert.Equal(1, provisioner.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FixedMediaProbe : IMediaProbe
    {
        public int Calls { get; private set; }

        public Task<MediaFileProbeResult> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new MediaFileProbeResult(
                TimeSpan.FromMinutes(2),
                "h264",
                "nv12",
                1920,
                1080,
                60,
                false,
                ["Full listening mix", "Game audio"]));
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
}
