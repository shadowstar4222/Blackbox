using Blackbox.Domain;
using Blackbox.Recording;
using Blackbox.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class SqliteSegmentRepositoryTests
{
    [Fact]
    public async Task Revision_changes_after_committed_writes_but_not_reads()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSegmentRepository(database);
        await repository.InitializeAsync();
        var revision = repository.Revision;
        await repository.GetAllAsync();
        await repository.GetMarkersAsync();
        Assert.Equal(revision, repository.Revision);

        var segment = TestSegments.Create("C:\\Recordings\\revision.mkv");
        await repository.UpsertAsync(segment);
        Assert.True(repository.Revision > revision);
        revision = repository.Revision;
        var marker = new TimelineMarker(Guid.NewGuid(), segment.SessionId, TimeSpan.Zero, "Moment", DateTimeOffset.UtcNow);
        await repository.AddMarkerAsync(marker);
        Assert.True(repository.Revision > revision);
        revision = repository.Revision;
        await repository.UpdateMarkerPlacementAsync(marker.Id, segment.SessionId, TimeSpan.FromSeconds(1));
        Assert.True(repository.Revision > revision);
        revision = repository.Revision;
        await repository.MarkProtectedRangeAsync(segment.StartTime, segment.EndTime);
        Assert.True(repository.Revision > revision);
        revision = repository.Revision;
        await repository.DeleteMarkerAsync(marker.Id);
        Assert.True(repository.Revision > revision);
        revision = repository.Revision;
        await repository.DeleteByIdAsync(segment.Id);
        Assert.True(repository.Revision > revision);
    }

    [Fact]
    public async Task UpsertAsync_persists_segment_metadata()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSegmentRepository(database);
        await repository.InitializeAsync();
        var segment = TestSegments.Create(filePath: "C:\\Recordings\\one.mkv");

        await repository.UpsertAsync(segment);

        var stored = await repository.GetAllAsync();
        Assert.Single(stored);
        Assert.Equal(segment.SessionId, stored[0].SessionId);
        Assert.Equal(segment.FilePath, stored[0].FilePath);
        Assert.True(await repository.ExistsByPathAsync(segment.FilePath));
    }

    [Fact]
    public async Task UpsertAsync_refreshes_indexed_metadata_without_removing_protection()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSegmentRepository(database);
        await repository.InitializeAsync();
        var original = TestSegments.Create(filePath: "C:\\Recordings\\indexed.mkv", isProtected: true);
        await repository.UpsertAsync(original);
        var updated = original with
        {
            SessionId = Guid.NewGuid(),
            EndTime = original.EndTime.AddMinutes(1),
            GameTitle = "Updated game",
            Encoder = "hevc",
            Width = 2560,
            Height = 1440,
            FileSizeBytes = 123456,
            IsProtected = false
        };

        await repository.UpsertAsync(updated);

        var stored = Assert.Single(await repository.GetAllAsync());
        Assert.Equal(updated.SessionId, stored.SessionId);
        Assert.Equal(updated.EndTime, stored.EndTime);
        Assert.Equal("Updated game", stored.GameTitle);
        Assert.Equal("hevc", stored.Encoder);
        Assert.Equal(2560, stored.Width);
        Assert.Equal(1440, stored.Height);
        Assert.Equal(123456, stored.FileSizeBytes);
        Assert.True(stored.IsProtected);
    }

    [Fact]
    public async Task InitializeAsync_allows_protection_before_recording_starts()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSegmentRepository(database);
        await repository.InitializeAsync();
        var service = new ProtectionService(
            repository,
            new FixedClock(DateTimeOffset.Parse("2026-07-15T12:00:00Z")),
            NullLogger<ProtectionService>.Instance);

        await service.ProtectPreviousFiveMinutesAsync();

        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task Repository_persists_markers_protected_ranges_and_damaged_media_state()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSegmentRepository(database);
        await repository.InitializeAsync();
        var segment = TestSegments.Create("C:\\Recordings\\damaged.mkv") with
        {
            IsDamaged = true,
            DamageDetail = "Container read failed"
        };
        await repository.UpsertAsync(segment);
        var marker = new TimelineMarker(
            Guid.NewGuid(),
            segment.SessionId,
            TimeSpan.FromSeconds(30),
            "Good play",
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(8),
            TimelineMarkerOrigin.AudioSpike,
            "Audio crossed the configured threshold.");

        await repository.AddMarkerAsync(marker);
        await repository.MarkProtectedRangeAsync(
            segment.StartTime.AddSeconds(10),
            segment.StartTime.AddSeconds(40));

        var stored = Assert.Single(await repository.GetAllAsync());
        Assert.True(stored.IsDamaged);
        Assert.Equal("Container read failed", stored.DamageDetail);
        Assert.True(stored.IsProtected);
        Assert.Equal(marker, Assert.Single(await repository.GetMarkersAsync()));
        var range = Assert.Single(await repository.GetProtectedRangesAsync());
        Assert.Equal(segment.StartTime.AddSeconds(10), range.StartTime);
        Assert.Equal(segment.StartTime.AddSeconds(40), range.EndTime);

        await repository.DeleteMarkerAsync(marker.Id);
        Assert.Empty(await repository.GetMarkersAsync());
    }

    [Fact]
    public async Task Repository_can_place_a_pending_live_marker_in_a_session()
    {
        var database = Path.Combine(Path.GetTempPath(), "blackbox-tests", $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSegmentRepository(database);
        await repository.InitializeAsync();
        var marker = new TimelineMarker(
            Guid.NewGuid(),
            Guid.Empty,
            TimeSpan.Zero,
            "Live marker",
            DateTimeOffset.UtcNow);
        var sessionId = Guid.NewGuid();
        await repository.AddMarkerAsync(marker);

        await repository.UpdateMarkerPlacementAsync(
            marker.Id,
            sessionId,
            TimeSpan.FromSeconds(42));

        var stored = Assert.Single(await repository.GetMarkersAsync());
        Assert.Equal(sessionId, stored.SessionId);
        Assert.Equal(TimeSpan.FromSeconds(42), stored.Offset);
    }
}
