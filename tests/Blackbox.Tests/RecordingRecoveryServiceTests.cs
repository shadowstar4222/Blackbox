using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RecordingRecoveryServiceTests
{
    private static readonly DateTime StableFileTimeUtc =
        DateTime.Parse("2026-07-16T12:00:00Z").ToUniversalTime();

    [Fact]
    public async Task RecoverAsync_atomically_publishes_valid_repair_and_preserves_original()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "2026-07-16 12-00-00.mkv");
            await WriteStableFileAsync(path, "broken-original");
            var repository = new InMemorySegmentRepository();
            var probe = new RecoveryProbe(path);
            var runner = new RecoveryCommandRunner(succeeds: true);
            var service = CreateService(root, repository, probe, runner);

            var result = await service.RecoverAsync();

            var recovered = Assert.Single(result.Files);
            Assert.Equal(RecordingRecoveryFileStatus.Recovered, recovered.Status);
            Assert.NotNull(recovered.PreservedOriginalPath);
            Assert.Equal("repaired-container", await File.ReadAllTextAsync(path));
            Assert.Equal("broken-original", await File.ReadAllTextAsync(recovered.PreservedOriginalPath));
            Assert.Equal(1, runner.RunCount);
            Assert.Equal(2, probe.ProbeCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoverAsync_leaves_original_untouched_when_remux_fails()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "2026-07-16 12-01-00.mkv");
            await WriteStableFileAsync(path, "irreplaceable-broken-data");
            var runner = new RecoveryCommandRunner(succeeds: false);
            var service = CreateService(
                root,
                new InMemorySegmentRepository(),
                new RecoveryProbe(path),
                runner);

            var result = await service.RecoverAsync();

            var damaged = Assert.Single(result.Files);
            Assert.Equal(RecordingRecoveryFileStatus.Damaged, damaged.Status);
            Assert.Contains("Recovery failed", damaged.Detail);
            Assert.Equal("irreplaceable-broken-data", await File.ReadAllTextAsync(path));
            Assert.Equal(1, runner.RunCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoverAsync_does_not_run_ffmpeg_for_healthy_media()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "2026-07-16 12-02-00.mkv");
            await WriteStableFileAsync(path, "healthy");
            var runner = new RecoveryCommandRunner(succeeds: true);
            var provisioner = new RecoveryProvisioner();
            var service = CreateService(
                root,
                new InMemorySegmentRepository(),
                new RecoveryProbe(),
                runner,
                provisioner);

            var result = await service.RecoverAsync();

            Assert.Equal(RecordingRecoveryFileStatus.Healthy, Assert.Single(result.Files).Status);
            Assert.Equal(0, runner.RunCount);
            Assert.Equal(0, provisioner.CallCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoverAsync_contains_recovery_tool_failure_and_checks_remaining_files()
    {
        var root = CreateRoot();
        try
        {
            var damagedPath = Path.Combine(root, "2026-07-16 12-02-30.mkv");
            var healthyPath = Path.Combine(root, "2026-07-16 12-02-31.mkv");
            await WriteStableFileAsync(damagedPath, "broken");
            await WriteStableFileAsync(healthyPath, "healthy");
            var probe = new RecoveryProbe(damagedPath);
            var runner = new RecoveryCommandRunner(succeeds: true);
            var service = CreateService(
                root,
                new InMemorySegmentRepository(),
                probe,
                runner,
                new FailingRecoveryProvisioner());

            var result = await service.RecoverAsync();

            Assert.Collection(
                result.Files,
                damaged =>
                {
                    Assert.Equal(RecordingRecoveryFileStatus.Damaged, damaged.Status);
                    Assert.Contains("Recovery tools failed", damaged.Detail);
                },
                healthy => Assert.Equal(RecordingRecoveryFileStatus.Healthy, healthy.Status));
            Assert.Equal(2, probe.ProbeCount);
            Assert.Equal(0, runner.RunCount);
            Assert.Equal("broken", await File.ReadAllTextAsync(damagedPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoverAsync_skips_recent_files_and_reconciles_missing_database_rows()
    {
        var root = CreateRoot();
        try
        {
            var activePath = Path.Combine(root, "2026-07-16 12-03-00.mkv");
            await File.WriteAllTextAsync(activePath, "still-recording");
            File.SetLastWriteTimeUtc(activePath, DateTime.Parse("2026-07-16T12:10:00Z").ToUniversalTime());
            var repository = new InMemorySegmentRepository();
            await repository.UpsertAsync(TestSegments.Create(Path.Combine(root, "missing.mkv")));
            var probe = new RecoveryProbe();
            var service = CreateService(
                root,
                repository,
                probe,
                new RecoveryCommandRunner(succeeds: true),
                options: new RecordingRecoveryOptions
                {
                    StabilityObservationDelay = TimeSpan.Zero,
                    MinimumFileAge = TimeSpan.FromHours(1)
                });

            var result = await service.RecoverAsync();

            Assert.Equal(1, result.ReconciledMissingFiles);
            Assert.Equal(RecordingRecoveryFileStatus.SkippedActive, Assert.Single(result.Files).Status);
            Assert.Equal(0, probe.ProbeCount);
            Assert.Empty(await repository.GetAllAsync());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static RecordingRecoveryService CreateService(
        string root,
        ISegmentRepository repository,
        IMediaProbe probe,
        IFfmpegCommandRunner runner,
        IFfmpegProvisioner? provisioner = null,
        RecordingRecoveryOptions? options = null) =>
        new(
            new RecordingSettings { RecordingLocation = root },
            repository,
            probe,
            provisioner ?? new RecoveryProvisioner(),
            runner,
            new FixedClock(DateTimeOffset.Parse("2026-07-16T12:10:00Z")),
            options ?? new RecordingRecoveryOptions
            {
                StabilityObservationDelay = TimeSpan.Zero,
                MinimumFileAge = TimeSpan.Zero
            },
            NullLogger<RecordingRecoveryService>.Instance);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteStableFileAsync(string path, string contents)
    {
        await File.WriteAllTextAsync(path, contents);
        File.SetLastWriteTimeUtc(path, StableFileTimeUtc);
    }

    private sealed class RecoveryProbe(params string[] damagedPaths) : IMediaProbe
    {
        private readonly HashSet<string> _damagedPaths = damagedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public int ProbeCount { get; private set; }

        public Task<MediaFileProbeResult> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            if (_damagedPaths.Contains(Path.GetFullPath(filePath)))
            {
                throw new InvalidDataException("container is incomplete");
            }

            return Task.FromResult(new MediaFileProbeResult(
                TimeSpan.FromMinutes(2),
                "h264",
                "yuv420p",
                1920,
                1080,
                60,
                false,
                ["Full listening mix", "Game audio"]));
        }
    }

    private sealed class RecoveryProvisioner : IFfmpegProvisioner
    {
        public int CallCount { get; private set; }

        public Task<FfmpegInstallation> EnsureInstalledAsync(
            IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new FfmpegInstallation("C:\\FFmpeg", "ffmpeg.exe", "ffprobe.exe", "ffplay.exe"));
        }
    }

    private sealed class FailingRecoveryProvisioner : IFfmpegProvisioner
    {
        public Task<FfmpegInstallation> EnsureInstalledAsync(
            IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("tool installation unavailable");
    }

    private sealed class RecoveryCommandRunner(bool succeeds) : IFfmpegCommandRunner
    {
        public int RunCount { get; private set; }

        public async Task RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan expectedDuration,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            if (!succeeds)
            {
                throw new InvalidOperationException("remux failed");
            }

            await File.WriteAllTextAsync(arguments[^1], "repaired-container", cancellationToken);
        }
    }
}
