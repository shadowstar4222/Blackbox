using Blackbox.Domain;
using Blackbox.Recording;
using Blackbox.Storage;

namespace Blackbox.Tests;

public sealed class SegmentScannerTests
{
    [Fact]
    public async Task ImportCompletedSegmentsAsync_imports_stable_mkv_files_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var sessionDirectory = RecordingDirectoryLayout.GetSessionDirectory(
            root,
            "Example Game",
            DateTimeOffset.Now);
        Directory.CreateDirectory(sessionDirectory);
        var segmentPath = Path.Combine(sessionDirectory, "segment-001.mkv");
        await File.WriteAllTextAsync(segmentPath, "fake media");
        File.SetLastWriteTimeUtc(segmentPath, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(2)));
        var repository = new SqliteSegmentRepository(Path.Combine(root, "blackbox.db"));
        await repository.InitializeAsync();
        var scanner = new SegmentScanner(repository);

        var firstImport = await scanner.ImportCompletedSegmentsAsync(root, TestSegments.Metadata());
        var secondImport = await scanner.ImportCompletedSegmentsAsync(root, TestSegments.Metadata());

        Assert.Equal(1, firstImport);
        Assert.Equal(0, secondImport);
        Assert.Single(await repository.GetAllAsync());
    }
}
