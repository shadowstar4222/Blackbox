using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class MarkerRetentionServiceTests
{
    [Fact]
    public async Task ReconcileAsync_owns_context_segments_until_marker_is_deleted()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "blackbox-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.Parse("2026-07-25T20:00:00Z");
        var segments = new InMemorySegmentRepository();
        var segment = TestSegments.Create(
            Path.Combine(root, "source.mkv"),
            now.AddMinutes(-3),
            TimeSpan.FromMinutes(2));
        await segments.UpsertAsync(segment);
        var marker = new TimelineMarker(
            Guid.NewGuid(),
            segment.SessionId,
            TimeSpan.FromMinutes(1),
            "Important moment",
            now,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(15));
        await segments.AddMarkerAsync(marker);
        var clips = new SqliteClipRepository(Path.Combine(root, "blackbox.db"));
        await clips.InitializeAsync();
        var countedClips = new CountingClipRepository(clips);
        var catalog = new RecordingSessionCatalog(segments);
        var library = new RecordingLibraryService(
            segments,
            new UnusedProbe(),
            new UnusedProvisioner(),
            catalog,
            new RecordingSettings { RecordingLocation = root },
            NullLogger<RecordingLibraryService>.Instance,
            new FixedClock(now),
            clips);
        var service = new MarkerRetentionService(
            segments,
            countedClips,
            catalog,
            library,
            new InstantClipOptions
            {
                OutputDirectory = Path.Combine(
                    root,
                    RecordingDirectoryLayout.InstantClipName)
            },
            new FixedClock(now),
            NullLogger<MarkerRetentionService>.Instance);

        try
        {
            await service.ReconcileAsync(recordingActive: false);

            Assert.Contains(segment.Id, await clips.GetRetainedSegmentIdsAsync());
            var markerReads = segments.MarkerReadCount;
            var segmentReads = segments.SegmentReadCount;
            await service.ReconcileAsync(recordingActive: false);
            Assert.Equal(1, countedClips.MarkerOwnershipWrites);
            Assert.Equal(markerReads, segments.MarkerReadCount);
            Assert.Equal(segmentReads, segments.SegmentReadCount);

            await service.ReconcileAsync(recordingActive: true);
            Assert.True(segments.MarkerReadCount > markerReads);

            var overlapping = segment with { Id = Guid.NewGuid(), FilePath = Path.Combine(root, "overlap.mkv") };
            await segments.UpsertAsync(overlapping);
            await service.ReconcileAsync(recordingActive: false);
            Assert.Equal(2, countedClips.MarkerOwnershipWrites);
            Assert.Contains(overlapping.Id, await clips.GetRetainedSegmentIdsAsync());

            await segments.DeleteByIdAsync(overlapping.Id);
            await service.ReconcileAsync(recordingActive: false);
            Assert.Equal(3, countedClips.MarkerOwnershipWrites);
            Assert.DoesNotContain(overlapping.Id, await clips.GetRetainedSegmentIdsAsync());

            await library.DeleteMarkerAsync(marker.Id);

            Assert.Empty(await clips.GetRetainedSegmentIdsAsync());
            Assert.Empty(await segments.GetMarkersAsync());
            await service.ReconcileAsync(recordingActive: false);
            markerReads = segments.MarkerReadCount;
            await service.ReconcileAsync(recordingActive: true);
            Assert.Equal(markerReads, segments.MarkerReadCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class UnusedProbe : IMediaProbe
    {
        public Task<MediaFileProbeResult> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The marker was already indexed.");
    }

    private sealed class UnusedProvisioner : IFfmpegProvisioner
    {
        public Task<FfmpegInstallation> EnsureInstalledAsync(
            IProgress<FfmpegProvisionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("FFmpeg should not be needed.");
    }
}
