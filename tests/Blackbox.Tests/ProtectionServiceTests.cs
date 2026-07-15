using Blackbox.Domain;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ProtectionServiceTests
{
    [Fact]
    public async Task ProtectPreviousFiveMinutesAsync_marks_overlapping_segments()
    {
        var now = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var repository = new InMemorySegmentRepository();
        var protectedSegment = TestSegments.Create(
            "C:\\Recordings\\recent.mkv",
            now.AddMinutes(-4),
            TimeSpan.FromMinutes(2));
        var oldSegment = TestSegments.Create(
            "C:\\Recordings\\old.mkv",
            now.AddMinutes(-20),
            TimeSpan.FromMinutes(2));
        await repository.UpsertAsync(protectedSegment);
        await repository.UpsertAsync(oldSegment);
        var service = new ProtectionService(repository, new FixedClock(now), NullLogger<ProtectionService>.Instance);

        await service.ProtectPreviousFiveMinutesAsync();

        var segments = await repository.GetAllAsync();
        Assert.True(segments.Single(segment => segment.FilePath == protectedSegment.FilePath).IsProtected);
        Assert.False(segments.Single(segment => segment.FilePath == oldSegment.FilePath).IsProtected);
    }
}
