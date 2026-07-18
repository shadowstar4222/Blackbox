using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Storage;
using Microsoft.Data.Sqlite;

namespace Blackbox.Tests;

public sealed class StorageConcurrencyTests
{
    [Fact]
    public async Task Repositories_handle_concurrent_segment_profile_and_marker_writes()
    {
        var root = CreateRoot();
        var database = Path.Combine(root, "blackbox.db");
        try
        {
            var segments = new SqliteSegmentRepository(database);
            var profiles = new SqliteGameProfileRepository(database);
            await segments.InitializeAsync();
            await profiles.InitializeAsync();
            var startedAt = DateTimeOffset.Parse("2026-07-18T12:00:00Z");

            var writes = Enumerable.Range(0, 40).Select(async index =>
            {
                var segment = TestSegments.Create(
                    Path.Combine(root, $"segment-{index:000}.mkv"),
                    startedAt.AddMinutes(index * 2));
                await segments.UpsertAsync(segment);
                await segments.AddMarkerAsync(new TimelineMarker(
                    Guid.NewGuid(),
                    segment.SessionId,
                    TimeSpan.FromSeconds(index),
                    $"Marker {index}",
                    startedAt.AddSeconds(index)));
                await profiles.UpsertAsync(new GameProfile(
                    $@"C:\Games\Stress\Game{index:000}.exe",
                    $"Stress Game {index}",
                    true,
                    startedAt,
                    startedAt));
            });

            await Task.WhenAll(writes);

            Assert.Equal(40, (await segments.GetAllAsync()).Count);
            Assert.Equal(40, (await segments.GetMarkersAsync()).Count);
            Assert.Equal(40, (await profiles.GetAllAsync()).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Segment_usage_registry_remains_balanced_under_parallel_leases()
    {
        var registry = new SegmentUsageRegistry();
        var segmentId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 250; iteration++)
            {
                var lease = registry.Acquire([segmentId, segmentId]);
                Assert.True(registry.IsInUse(segmentId));
                lease.Dispose();
                lease.Dispose();
            }
        })));

        Assert.False(registry.IsInUse(segmentId));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
