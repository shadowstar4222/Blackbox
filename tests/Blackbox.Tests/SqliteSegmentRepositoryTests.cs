using Blackbox.Domain;
using Blackbox.Storage;

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
}
