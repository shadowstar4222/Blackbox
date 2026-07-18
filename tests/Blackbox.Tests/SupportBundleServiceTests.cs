using System.IO.Compression;
using System.Text.Json;
using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class SupportBundleServiceTests
{
    [Fact]
    public async Task ExportAsync_creates_bounded_privacy_reviewed_archive()
    {
        var root = CreateRoot();
        try
        {
            var destination = Path.Combine(root, "support.zip");
            var entries = Enumerable.Range(0, 8)
                .Select(index => new DiagnosticLogEntry(
                    DateTimeOffset.Parse("2026-07-18T12:00:00Z").AddSeconds(index),
                    DiagnosticCategory.Recording,
                    DiagnosticSeverity.Information,
                    index == 7
                        ? @"OBS password=top-secret wrote C:\Users\shado\Videos\Blackbox\clip.mkv"
                        : $"Recording event {index}.",
                    @"C:\Users\shado\AppData\Local\Blackbox\logs\blackbox.log"))
                .ToArray();
            var service = new SupportBundleService(
                new FixedClock(DateTimeOffset.Parse("2026-07-18T13:00:00Z")),
                new SupportBundleOptions { MaximumLogEntries = 3 });

            var result = await service.ExportAsync(
                destination,
                new SupportBundleRequest(
                    false,
                    true,
                    12,
                    1,
                    2,
                    4096,
                    3,
                    "Recovery is healthy.",
                    entries));

            Assert.Equal(3, result.IncludedLogEntries);
            Assert.True(result.RedactionCount >= 2);
            Assert.True(result.FileSizeBytes > 0);
            using var archive = ZipFile.OpenRead(destination);
            Assert.Equal(
                ["diagnostics.json", "PRIVACY.txt", "recent-events.json"],
                archive.Entries.Select(static entry => entry.FullName).Order().ToArray());
            var contents = string.Join(
                '\n',
                archive.Entries.Select(ReadEntry));
            Assert.DoesNotContain("top-secret", contents, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\Users\shado", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Recording event 0.", contents, StringComparison.Ordinal);
            Assert.Contains("<redacted>", contents, StringComparison.Ordinal);
            Assert.Contains("%USERPROFILE%", contents, StringComparison.Ordinal);
            Assert.Contains("Video and audio recordings", contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExportAsync_cancellation_leaves_no_partial_archive()
    {
        var root = CreateRoot();
        try
        {
            var destination = Path.Combine(root, "support.zip");
            var service = new SupportBundleService(
                new FixedClock(DateTimeOffset.Parse("2026-07-18T13:00:00Z")),
                new SupportBundleOptions());
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(
                destination,
                new SupportBundleRequest(false, false, 0, 0, 0, 0, 0, "None", []),
                cancellation.Token));

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
