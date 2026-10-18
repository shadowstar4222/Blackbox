using Blackbox.Capture.Worker;

namespace Blackbox.Capture.Tests;

public sealed class SegmentFinalizerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blackbox-segments", Guid.NewGuid().ToString("N"));
    private const string Prefix = "2026-09-12 10-00-00-12345678901234567890123456789012";

    public SegmentFinalizerTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Partial_csv_rows_are_deferred_and_closed_bytes_counted_once()
    {
        var finalizer = new SegmentFinalizer(directory, Prefix);
        var first = Path.Combine(directory, Prefix + "-0000.active.mkv");
        File.WriteAllBytes(first, new byte[100]);
        File.WriteAllText(finalizer.CsvPath, $"{Prefix}-0000.active.mkv,0,5");
        finalizer.Poll();
        Assert.Equal(0, finalizer.FinalizedBytes);
        Assert.True(File.Exists(first));
        File.AppendAllText(finalizer.CsvPath, "\n");
        finalizer.Poll();
        finalizer.Poll();
        Assert.False(File.Exists(first));
        Assert.Equal(100, finalizer.FinalizedBytes);
        Assert.Equal(1, finalizer.NextIndex);
        Assert.EndsWith("-0000.mkv", finalizer.LastFinalizedPath);
        File.WriteAllBytes(Path.Combine(directory, Prefix + "-0001.active.mkv"), new byte[200]);
        File.AppendAllText(finalizer.CsvPath, $"{Prefix}-0001.active.mkv,5,10\n");
        finalizer.Poll();
        Assert.Equal(300, finalizer.FinalizedBytes);
        Assert.Equal(2, finalizer.NextIndex);
    }

    [Fact]
    public void Unexpected_index_is_rejected_without_renaming()
    {
        var finalizer = new SegmentFinalizer(directory, Prefix);
        var active = Path.Combine(directory, Prefix + "-0002.active.mkv");
        File.WriteAllBytes(active, new byte[10]);
        File.WriteAllText(finalizer.CsvPath, $"{Prefix}-0002.active.mkv,0,5\n");
        Assert.Throws<IOException>(finalizer.Poll);
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void Truncated_csv_is_rejected_instead_of_silently_skipping_future_rows()
    {
        var finalizer = new SegmentFinalizer(directory, Prefix);
        File.WriteAllText(finalizer.CsvPath, "unfinished row");
        finalizer.Poll();
        File.WriteAllText(finalizer.CsvPath, string.Empty);
        Assert.Throws<IOException>(finalizer.Poll);
        Assert.Equal(0, finalizer.NextIndex);
    }

    [Fact]
    public void Finalization_never_overwrites_existing_recordings()
    {
        var finalizer = new SegmentFinalizer(directory, Prefix);
        File.WriteAllBytes(Path.Combine(directory, Prefix + "-0000.active.mkv"), new byte[10]);
        var existing = Path.Combine(directory, Prefix + "-0000.mkv");
        File.WriteAllBytes(existing, [42]);
        File.WriteAllText(finalizer.CsvPath, $"{Prefix}-0000.active.mkv,0,5\n");
        Assert.Throws<IOException>(finalizer.Poll);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(existing));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
