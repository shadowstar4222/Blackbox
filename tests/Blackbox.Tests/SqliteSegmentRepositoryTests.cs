using Blackbox.Domain;
using Blackbox.Recording;
using Blackbox.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class SqliteSegmentRepositoryTests
{
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
}
