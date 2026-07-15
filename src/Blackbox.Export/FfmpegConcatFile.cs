using System.Globalization;
using System.Text;
using Blackbox.Domain;

namespace Blackbox.Export;

internal static class FfmpegConcatFile
{
    public static string Create(string workDirectory, IReadOnlyList<RecordingSegment> segments)
    {
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, $"session-{Guid.NewGuid():N}.ffconcat");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("ffconcat version 1.0");
        foreach (var segment in segments.OrderBy(static segment => segment.StartTime))
        {
            var escapedPath = Path.GetFullPath(segment.FilePath)
                .Replace('\\', '/')
                .Replace("'", "'\\''", StringComparison.Ordinal);
            writer.WriteLine($"file '{escapedPath}'");
            writer.WriteLine($"duration {(segment.EndTime - segment.StartTime).TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)}");
        }

        return path;
    }
}
