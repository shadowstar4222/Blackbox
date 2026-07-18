using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class DiagnosticLogReaderTests
{
    [Fact]
    public async Task GetRecentAsync_parses_severity_and_operational_categories()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(root, "blackbox-20260716.log"),
                [
                    "2026-07-16 12:00:00.000 -04:00 [INF] Startup recovery completed with 1 repaired file.",
                    "2026-07-16 12:01:00.000 -04:00 [WRN] Automatic capture detected no remembered game.",
                    "2026-07-16 12:02:00.000 -04:00 [ERR] Export failed while FFmpeg was rendering."
                ]);
            var reader = new DiagnosticLogReader(new DiagnosticLogOptions { LogDirectory = root });

            var entries = await reader.GetRecentAsync();

            Assert.Equal(3, entries.Count);
            Assert.Equal(DiagnosticCategory.Export, entries[0].Category);
            Assert.Equal(DiagnosticSeverity.Error, entries[0].Severity);
            Assert.Equal(DiagnosticCategory.Detection, entries[1].Category);
            Assert.Equal(DiagnosticSeverity.Warning, entries[1].Severity);
            Assert.Equal(DiagnosticCategory.Recovery, entries[2].Category);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetRecentAsync_bounds_large_files_and_returns_the_newest_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var lines = Enumerable.Range(0, 300)
                .Select(index =>
                    $"2026-07-16 12:00:{index % 60:00}.000 -04:00 [INF] Recording event {index:000}.")
                .ToArray();
            await File.WriteAllLinesAsync(Path.Combine(root, "blackbox-20260716.log"), lines);
            var reader = new DiagnosticLogReader(new DiagnosticLogOptions
            {
                LogDirectory = root,
                MaximumBytesPerFile = 4096
            });

            var entries = await reader.GetRecentAsync(10);

            Assert.Equal(10, entries.Count);
            Assert.Contains(entries, entry => entry.Message.Contains("event 299", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry => entry.Message.Contains("event 001", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
