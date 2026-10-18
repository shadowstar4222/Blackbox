using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class InstantClipServiceTests
{
    [Fact]
    public async Task Empty_queue_polls_do_no_repository_work_and_create_wakes_the_worker()
    {
        using var fixture = await ClipFixture.CreateAsync(new WritingRunner());
        await fixture.Service.ProcessPendingAsync();
        await fixture.Service.ProcessPendingAsync();
        await fixture.Service.ProcessPendingAsync();
        Assert.Equal(1, fixture.Queue.GetAllCount);

        var clip = await fixture.Service.CreateAsync(TimeSpan.FromSeconds(30));
        await fixture.Service.ProcessPendingAsync();
        Assert.Equal(InstantClipStatus.Ready, (await fixture.Clips.GetByIdAsync(clip.Id))!.Status);
        Assert.Equal(2, fixture.Queue.GetAllCount);

        await fixture.Service.ProcessPendingAsync();
        await fixture.Service.ProcessPendingAsync();
        Assert.Equal(3, fixture.Queue.GetAllCount);
    }

    [Fact]
    public async Task Retry_wakes_an_idle_worker_and_preserves_failed_clip_ownership()
    {
        using var fixture = await ClipFixture.CreateAsync(new ThrowingRunner());
        var clip = await fixture.Service.CreateAsync(TimeSpan.FromSeconds(30));
        await fixture.Service.ProcessPendingAsync();
        var failed = (await fixture.Clips.GetByIdAsync(clip.Id))!;
        await fixture.Service.ProcessPendingAsync();
        var readCount = fixture.Queue.GetAllCount;

        await fixture.Service.RetryAsync(clip.Id);

        Assert.Equal(readCount + 1, fixture.Queue.GetAllCount);
        Assert.Equal(failed.SourceSegmentIds, (await fixture.Clips.GetByIdAsync(clip.Id))!.SourceSegmentIds);
        Assert.NotEmpty(await fixture.Clips.GetRetainedSegmentIdsAsync());
    }

    [Fact]
    public async Task ProcessPendingAsync_indexes_new_segment_and_finalizes_clip()
    {
        var fixture = await ClipFixture.CreateAsync(new WritingRunner());
        try
        {
            var clip = await fixture.Service.CreateAsync(TimeSpan.FromMinutes(1), "Great play");

            await fixture.Service.ProcessPendingAsync();

            var ready = await fixture.Clips.GetByIdAsync(clip.Id);
            Assert.NotNull(ready);
            Assert.Equal(InstantClipStatus.Ready, ready.Status);
            Assert.Equal("Example Game", ready.GameTitle);
            Assert.True(File.Exists(ready.OutputPath));
            Assert.NotEmpty(ready.SourceSegmentIds);
            Assert.Empty(await fixture.Clips.GetRetainedSegmentIdsAsync());
            Assert.Equal(
                TimeSpan.FromMinutes(1),
                ready.ActualEndTime - ready.ActualStartTime);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Failed_finalization_keeps_source_segments_for_retry()
    {
        var fixture = await ClipFixture.CreateAsync(new ThrowingRunner());
        try
        {
            var clip = await fixture.Service.CreateAsync(TimeSpan.FromSeconds(30));

            await fixture.Service.ProcessPendingAsync();

            var failed = await fixture.Clips.GetByIdAsync(clip.Id);
            Assert.NotNull(failed);
            Assert.Equal(InstantClipStatus.Failed, failed.Status);
            Assert.Contains("test encoder failure", failed.FailureDetail);
            Assert.NotEmpty(failed.SourceSegmentIds);
            Assert.Equal(
                failed.SourceSegmentIds.Order(),
                (await fixture.Clips.GetRetainedSegmentIdsAsync()).Order());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private sealed class ClipFixture : IDisposable
    {
        private ClipFixture(
            string root,
            SqliteClipRepository clips,
            CountingClipRepository queue,
            InstantClipService service)
        {
            Root = root;
            Clips = clips;
            Queue = queue;
            Service = service;
        }

        public string Root { get; }
        public SqliteClipRepository Clips { get; }
        public CountingClipRepository Queue { get; }
        public InstantClipService Service { get; }

        public static async Task<ClipFixture> CreateAsync(IFfmpegCommandRunner runner)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "blackbox-tests",
                Guid.NewGuid().ToString("N"));
            var now = new DateTimeOffset(
                DateTime.SpecifyKind(new DateTime(2026, 7, 25, 20, 0, 0), DateTimeKind.Local));
            var sourceDirectory = RecordingDirectoryLayout.GetSessionDirectory(
                root,
                "Example Game",
                now);
            Directory.CreateDirectory(sourceDirectory);
            var sourceStart = now.AddMinutes(-2);
            var sourcePath = Path.Combine(
                sourceDirectory,
                $"{sourceStart.LocalDateTime:yyyy-MM-dd HH-mm-ss}.mkv");
            await File.WriteAllTextAsync(sourcePath, "source");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-2));

            var database = Path.Combine(root, "blackbox.db");
            var segments = new SqliteSegmentRepository(database);
            var clips = new SqliteClipRepository(database);
            await segments.InitializeAsync();
            await clips.InitializeAsync();
            var catalog = new RecordingSessionCatalog(segments);
            var provisioner = new FixedProvisioner(root);
            var ffmpegOptions = new FfmpegOptions
            {
                RootDirectory = root,
                WorkDirectory = Path.Combine(root, "work")
            };
            var exportService = new SessionExportService(
                provisioner,
                runner,
                new SegmentUsageRegistry(),
                ffmpegOptions,
                NullLogger<SessionExportService>.Instance);
            var library = new RecordingLibraryService(
                segments,
                new FixedProbe(),
                provisioner,
                catalog,
                new RecordingSettings { RecordingLocation = root },
                NullLogger<RecordingLibraryService>.Instance,
                new FixedClock(now));
            var queue = new CountingClipRepository(clips);
            var service = new InstantClipService(
                queue,
                catalog,
                library,
                exportService,
                new InstantClipOptions
                {
                    OutputDirectory = Path.Combine(
                        root,
                        RecordingDirectoryLayout.InstantClipName),
                    LibraryRefreshInterval = TimeSpan.FromSeconds(1)
                },
                new FixedClock(now),
                NullLogger<InstantClipService>.Instance);
            return new ClipFixture(root, clips, queue, service);
        }

        public void Dispose()
        {
            Service.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class FixedProbe : IMediaProbe
    {
        public Task<MediaFileProbeResult> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaFileProbeResult(
                TimeSpan.FromMinutes(2),
                "h264",
                "nv12",
                1920,
                1080,
                60,
                false,
                ["Full listening mix", "Game audio"]));
    }

    private sealed class FixedProvisioner(string root) : IFfmpegProvisioner
    {
        public Task<FfmpegInstallation> EnsureInstalledAsync(
            IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfmpegInstallation(root, "ffmpeg", "ffprobe"));
    }

    private sealed class WritingRunner : IFfmpegCommandRunner
    {
        public Task RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan expectedDuration,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            File.WriteAllTextAsync(arguments[^1], "clip", cancellationToken);
    }

    private sealed class ThrowingRunner : IFfmpegCommandRunner
    {
        public Task RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan expectedDuration,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("test encoder failure");
    }
}
