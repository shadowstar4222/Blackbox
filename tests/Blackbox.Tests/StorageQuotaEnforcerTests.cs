using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class StorageQuotaEnforcerTests
{
    [Fact]
    public async Task EnforceAsync_deletes_oldest_unprotected_segments_when_quota_is_exceeded()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var repository = new InMemorySegmentRepository();
        var oldPath = CreateSegmentFile(root, "old.mkv");
        var newPath = CreateSegmentFile(root, "new.mkv");
        await repository.UpsertAsync(TestSegments.Create(oldPath, now.AddMinutes(-10), fileSizeBytes: 800));
        await repository.UpsertAsync(TestSegments.Create(newPath, now.AddMinutes(-2), fileSizeBytes: 800));
        var enforcer = new StorageQuotaEnforcer(repository, new FixedClock(now), NullLogger<StorageQuotaEnforcer>.Instance);

        var result = await enforcer.EnforceAsync(new RecordingSettings
        {
            RecordingLocation = root,
            MaximumStorageGigabytes = 0.000001m,
            MaximumRetainedDuration = TimeSpan.FromHours(1)
        });

        Assert.Equal(1, result.DeletedSegments);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
        Assert.Single(await repository.GetAllAsync());
    }

    [Fact]
    public async Task EnforceAsync_never_deletes_protected_segments()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var repository = new InMemorySegmentRepository();
        var protectedPath = CreateSegmentFile(root, "protected.mkv");
        var newerPath = CreateSegmentFile(root, "newer.mkv");
        await repository.UpsertAsync(TestSegments.Create(protectedPath, now.AddMinutes(-10), fileSizeBytes: 800, isProtected: true));
        await repository.UpsertAsync(TestSegments.Create(newerPath, now.AddMinutes(-2), fileSizeBytes: 800));
        var enforcer = new StorageQuotaEnforcer(repository, new FixedClock(now), NullLogger<StorageQuotaEnforcer>.Instance);

        await enforcer.EnforceAsync(new RecordingSettings
        {
            RecordingLocation = root,
            MaximumStorageGigabytes = 0.000001m,
            MaximumRetainedDuration = TimeSpan.FromHours(1)
        });

        Assert.True(File.Exists(protectedPath));
        Assert.False(File.Exists(newerPath));
        Assert.Single((await repository.GetAllAsync()).Where(static segment => segment.IsProtected));
    }

    [Fact]
    public async Task EnforceAsync_deletes_unprotected_segments_when_minimum_free_space_is_not_met()
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var repository = new InMemorySegmentRepository();
        var path = CreateSegmentFile(root, "segment.mkv");
        await repository.UpsertAsync(TestSegments.Create(path, now.AddMinutes(-1), fileSizeBytes: 800));
        var enforcer = new StorageQuotaEnforcer(repository, new FixedClock(now), NullLogger<StorageQuotaEnforcer>.Instance);

        var result = await enforcer.EnforceAsync(new RecordingSettings
        {
            RecordingLocation = root,
            MaximumStorageGigabytes = 1000,
            MaximumRetainedDuration = TimeSpan.FromHours(1),
            MinimumRemainingFreeDiskSpaceGigabytes = 1_000_000
        });

        Assert.Equal(1, result.DeletedSegments);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EnforceAsync_does_not_delete_a_segment_in_use_by_playback_or_export()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = CreateSegmentFile(root, "leased.mkv");
        try
        {
            var repository = new InMemorySegmentRepository();
            var segment = TestSegments.Create(path, fileSizeBytes: 1024);
            await repository.UpsertAsync(segment);
            var registry = new SegmentUsageRegistry();
            using var lease = registry.Acquire([segment.Id]);
            var enforcer = new StorageQuotaEnforcer(
                repository,
                new FixedClock(DateTimeOffset.UtcNow),
                NullLogger<StorageQuotaEnforcer>.Instance,
                registry);
            var settings = new RecordingSettings
            {
                RecordingLocation = root,
                MaximumStorageGigabytes = 0.0000001m,
                MinimumRemainingFreeDiskSpaceGigabytes = 0
            };

            var result = await enforcer.EnforceAsync(settings);

            Assert.Equal(0, result.DeletedSegments);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateSegmentFile(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, new byte[800]);
        return path;
    }
}
