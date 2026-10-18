using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RecordingLibraryServiceTests
{
    [Fact]
    public async Task RefreshAsync_indexes_adjacent_files_and_reuses_unchanged_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var sessionDirectory = RecordingDirectoryLayout.GetSessionDirectory(
            root,
            "Example Game",
            DateTimeOffset.Parse("2026-07-15T12:00:00Z"));
        Directory.CreateDirectory(sessionDirectory);
        var firstPath = Path.Combine(sessionDirectory, "2026-07-15 12-00-00.mkv");
        var secondPath = Path.Combine(sessionDirectory, "2026-07-15 12-02-00.mkv");
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
            Assert.Equal("Example Game", session.GameTitle);
            Assert.Single(secondRefresh);
            Assert.Equal(2, probe.Calls);
            Assert.Equal(1, provisioner.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefreshAsync_keeps_unreadable_media_visible_as_damaged()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "2026-07-15 12-00-00.mkv");
        await File.WriteAllTextAsync(path, "damaged");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-2));
        try
        {
            var repository = new InMemorySegmentRepository();
            var service = new RecordingLibraryService(
                repository,
                new FailingMediaProbe(),
                new FixedProvisioner(root),
                new RecordingSessionCatalog(repository),
                new RecordingSettings { RecordingLocation = root },
                NullLogger<RecordingLibraryService>.Instance);

            var session = Assert.Single(await service.RefreshAsync());

            Assert.True(session.HasDamagedSegments);
            Assert.Contains("Unreadable test media", session.Segments[0].DamageDetail);
            Assert.True(File.Exists(session.Segments[0].FilePath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefreshAsync_does_not_index_generated_instant_clips()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var recordingDirectory = RecordingDirectoryLayout.GetSessionDirectory(
            root,
            "Example Game",
            DateTimeOffset.Now);
        var clipDirectory = Path.Combine(
            root,
            RecordingDirectoryLayout.InstantClipName,
            "Example Game",
            DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(recordingDirectory);
        Directory.CreateDirectory(clipDirectory);
        var recordingPath = Path.Combine(recordingDirectory, "2026-07-25 12-00-00.mkv");
        var clipPath = Path.Combine(clipDirectory, "highlight.mkv");
        await File.WriteAllTextAsync(recordingPath, "recording");
        await File.WriteAllTextAsync(clipPath, "clip");
        File.SetLastWriteTimeUtc(recordingPath, DateTime.UtcNow.AddSeconds(-2));
        File.SetLastWriteTimeUtc(clipPath, DateTime.UtcNow.AddSeconds(-2));
        try
        {
            var repository = new InMemorySegmentRepository();
            var service = new RecordingLibraryService(
                repository,
                new FixedMediaProbe(),
                new FixedProvisioner(root),
                new RecordingSessionCatalog(repository),
                new RecordingSettings { RecordingLocation = root },
                NullLogger<RecordingLibraryService>.Instance);

            var session = Assert.Single(await service.RefreshAsync());

            Assert.Single(session.Segments);
            Assert.Equal(Path.GetFullPath(recordingPath), session.Segments[0].FilePath);
            Assert.DoesNotContain(
                await repository.GetAllAsync(),
                segment => segment.FilePath.Equals(clipPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefreshAsync_places_pending_live_markers_on_the_indexed_timeline()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "2026-07-15 12-00-00.mkv");
        await File.WriteAllTextAsync(path, "recording");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-2));
        try
        {
            var repository = new InMemorySegmentRepository();
            var localStart = new DateTimeOffset(
                DateTime.SpecifyKind(
                    new DateTime(2026, 7, 15, 12, 0, 0),
                    DateTimeKind.Local));
            var marker = new TimelineMarker(
                Guid.NewGuid(),
                Guid.Empty,
                TimeSpan.Zero,
                "Live marker",
                localStart.AddMinutes(1));
            await repository.AddMarkerAsync(marker);
            var service = new RecordingLibraryService(
                repository,
                new FixedMediaProbe(),
                new FixedProvisioner(root),
                new RecordingSessionCatalog(repository),
                new RecordingSettings { RecordingLocation = root },
                NullLogger<RecordingLibraryService>.Instance);

            var session = Assert.Single(await service.RefreshAsync());

            var placedMarker = Assert.Single(session.Markers);
            Assert.Equal(marker.Id, placedMarker.Id);
            Assert.Equal(session.Id, placedMarker.SessionId);
            Assert.Equal(TimeSpan.FromMinutes(1), placedMarker.Offset);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefreshAsync_removes_unmatched_live_markers_after_retention_period()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "2026-07-15 12-00-00.mkv");
        await File.WriteAllTextAsync(path, "recording");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-2));
        try
        {
            var repository = new InMemorySegmentRepository();
            await repository.AddMarkerAsync(new TimelineMarker(
                Guid.NewGuid(),
                Guid.Empty,
                TimeSpan.Zero,
                "Unmatched live marker",
                DateTimeOffset.Parse("2026-07-01T12:00:00Z")));
            var service = new RecordingLibraryService(
                repository,
                new FixedMediaProbe(),
                new FixedProvisioner(root),
                new RecordingSessionCatalog(repository),
                new RecordingSettings { RecordingLocation = root },
                NullLogger<RecordingLibraryService>.Instance,
                new FixedClock(DateTimeOffset.Parse("2026-07-30T12:00:00Z")));

            await service.RefreshAsync();

            Assert.Empty(await repository.GetMarkersAsync());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Native_segments_use_closed_csv_ranges_and_skip_active_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "Native Game", "2026-09-12");
        Directory.CreateDirectory(directory);
        var prefix = "2026-09-12 12-00-00-" + Guid.NewGuid().ToString("N");
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(directory, $"{prefix}-{i:0000}{(i == 2 ? ".active" : "")}.mkv");
                await File.WriteAllTextAsync(path, "sample");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, prefix + ".segments.csv"),
                $"{prefix}-0000.active.mkv,0,5\n{prefix}-0001.active.mkv,5,10\n");
            var repository = new InMemorySegmentRepository();
            var probe = new FixedMediaProbe();
            var service = new RecordingLibraryService(repository, probe, new FixedProvisioner(root),
                new RecordingSessionCatalog(repository), new RecordingSettings { RecordingLocation = root },
                NullLogger<RecordingLibraryService>.Instance);
            var session = Assert.Single(await service.RefreshAsync());
            Assert.Equal(2, session.Segments.Count);
            Assert.Equal(TimeSpan.FromSeconds(10), session.Duration);
            Assert.Equal(session.Segments[0].EndTime, session.Segments[1].StartTime);
            await service.RefreshAsync();
            Assert.Equal(2, probe.Calls);
        }
        finally { Directory.Delete(root, true); }
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
            return Task.FromResult(new FfmpegInstallation(root, "ffmpeg", "ffprobe"));
        }
    }

    private sealed class FailingMediaProbe : IMediaProbe
    {
        public Task<MediaFileProbeResult> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidDataException("Unreadable test media.");
    }
}
