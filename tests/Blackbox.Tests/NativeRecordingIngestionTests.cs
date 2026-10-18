using Blackbox.Domain;
using Blackbox.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class NativeRecordingIngestionTests
{
    [Theory]
    [InlineData(".active.mkv")]
    [InlineData(".partial.mkv")]
    public async Task Refresh_never_probes_or_indexes_active_or_partial_files(string suffix)
    {
        using var fixture = new Fixture();
        var path = fixture.Write($"{fixture.Prefix}-0000{suffix}");

        Assert.Empty(await fixture.Library.RefreshAsync());
        Assert.Empty(await fixture.Library.RefreshAsync());

        Assert.Equal(0, fixture.Probe.Calls);
        Assert.Equal(0, fixture.Provisioner.Calls);
        Assert.Equal(0, fixture.Repository.UpsertCount);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Refresh_does_not_probe_a_legacy_file_with_an_open_writer()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("2026-07-15 12-00-00.mkv");
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Empty(await fixture.Library.RefreshAsync());
        Assert.Equal(0, fixture.Probe.Calls);
        Assert.Equal(0, fixture.Provisioner.Calls);
    }

    [Fact]
    public async Task Refresh_uses_csv_offsets_and_native_identity_even_with_missing_segments()
    {
        using var fixture = new Fixture();
        var first = fixture.Write($"{fixture.Prefix}-0001.mkv");
        var second = fixture.Write($"{fixture.Prefix}-0003.mkv");
        File.SetCreationTimeUtc(first, DateTime.UtcNow);
        File.SetCreationTimeUtc(second, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(fixture.Root, fixture.Prefix + ".segments.csv"),
            $"\"{fixture.Prefix}-0001.active.mkv\",120.25,240.25\n" +
            $"\"{fixture.Prefix}-0003.active.mkv\",361.5,481.5\n" +
            "\"incomplete");

        var session = Assert.Single(await fixture.Library.RefreshAsync());
        Assert.Equal(fixture.RecordingId, session.Id);
        Assert.Equal(fixture.Start.AddSeconds(120.25), session.Segments[0].StartTime);
        Assert.Equal(fixture.Start.AddSeconds(361.5), session.Segments[1].StartTime);
        Assert.True(session.HasGaps);

        await fixture.Library.RefreshAsync();
        Assert.Equal(2, fixture.Probe.Calls);
        Assert.Equal(2, fixture.Repository.UpsertCount);
    }

    [Fact]
    public async Task Refresh_falls_back_to_cumulative_durations_and_keeps_distinct_native_sessions()
    {
        using var fixture = new Fixture();
        fixture.Probe.Duration = TimeSpan.FromSeconds(121.5);
        fixture.Write($"{fixture.Prefix}-0000.mkv");
        fixture.Write($"{fixture.Prefix}-0001.mkv");
        fixture.Write($"2026-07-15 12-04-03-{Guid.NewGuid():N}-0000.mkv");

        var sessions = await fixture.Library.RefreshAsync();

        Assert.Equal(2, sessions.Count);
        var session = Assert.Single(sessions, session => session.Id == fixture.RecordingId);
        Assert.Equal(fixture.Start, session.StartTime);
        Assert.Equal(fixture.Start.AddSeconds(121.5), session.Segments[1].StartTime);
    }

    [Fact]
    public async Task Refresh_preserves_existing_ids_timestamps_audio_layout_and_protection()
    {
        using var fixture = new Fixture();
        var path = fixture.Write($"{fixture.Prefix}-0002.mkv");
        var existing = TestSegments.Create(path, fixture.Start.AddMinutes(4),
            fileSizeBytes: new FileInfo(path).Length, isProtected: true);
        await fixture.Repository.UpsertAsync(existing);

        var session = Assert.Single(await fixture.Library.RefreshAsync());

        Assert.Equal(existing, Assert.Single(session.Segments));
        Assert.Equal(existing.SessionId, session.Id);
        Assert.Equal(0, fixture.Probe.Calls);
        Assert.Equal(1, fixture.Repository.UpsertCount);

        File.AppendAllText(path, "repair");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-2));
        var repaired = Assert.Single((await fixture.Library.RefreshAsync()).Single().Segments);
        Assert.Equal(existing.Id, repaired.Id);
        Assert.Equal(existing.SessionId, repaired.SessionId);
        Assert.Equal(existing.StartTime, repaired.StartTime);
        Assert.True(repaired.IsProtected);
    }

    [Fact]
    public async Task Refresh_only_retries_damaged_files_when_the_file_changes_including_same_size_edits()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("2026-07-15 12-00-00.mkv");
        fixture.Probe.Fail = true;
        var damaged = Assert.Single((await fixture.Library.RefreshAsync()).Single().Segments);
        await fixture.Library.RefreshAsync();
        Assert.Equal(1, fixture.Probe.Calls);
        Assert.Equal(1, fixture.Repository.UpsertCount);

        fixture.Probe.Fail = false;
        File.WriteAllText(path, "RECORDING");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-5));
        var repaired = Assert.Single((await fixture.Library.RefreshAsync()).Single().Segments);
        Assert.Equal(2, fixture.Probe.Calls);
        Assert.False(repaired.IsDamaged);
        Assert.Equal(damaged.Id, repaired.Id);
        Assert.Equal(damaged.SessionId, repaired.SessionId);
    }

    [Fact]
    public async Task Concurrent_refreshes_probe_a_closed_segment_only_once()
    {
        using var fixture = new Fixture();
        fixture.Write($"{fixture.Prefix}-0000.mkv");

        await Task.WhenAll(fixture.Library.RefreshAsync(), fixture.Library.RefreshAsync());

        Assert.Equal(1, fixture.Probe.Calls);
        Assert.Equal(1, fixture.Repository.UpsertCount);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        public Guid RecordingId { get; } = Guid.NewGuid();
        public string Prefix => $"2026-07-15 12-00-00-{RecordingId:N}";
        public DateTimeOffset Start { get; } = new(DateTime.SpecifyKind(new DateTime(2026, 7, 15, 12, 0, 0), DateTimeKind.Local));
        public InMemorySegmentRepository Repository { get; } = new();
        public Probe Probe { get; } = new();
        public Provisioner Provisioner { get; } = new();
        public RecordingLibraryService Library { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Library = new RecordingLibraryService(Repository, Probe, Provisioner,
                new RecordingSessionCatalog(Repository), new RecordingSettings { RecordingLocation = Root },
                NullLogger<RecordingLibraryService>.Instance);
        }

        public string Write(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "recording");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-2));
            return path;
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Probe : IMediaProbe
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(2);
        public async Task<MediaFileProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            if (Fail) throw new InvalidDataException("damaged test recording");
            return new MediaFileProbeResult(Duration, "h264", "nv12", 1920, 1080, 60, false, ["Game audio"]);
        }
    }

    private sealed class Provisioner : IFfmpegProvisioner
    {
        public int Calls { get; private set; }
        public Task<FfmpegInstallation> EnsureInstalledAsync(IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new FfmpegInstallation("unused", "ffmpeg", "ffprobe"));
        }
    }
}
