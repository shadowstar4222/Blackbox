using System.Globalization;

namespace Blackbox.Export;

internal static class TimelineAssetArgumentBuilder
{
    public static IReadOnlyList<string> BuildThumbnails(
        string concatPath,
        string outputPattern,
        TimeSpan duration)
    {
        var interval = Math.Max(1, duration.TotalSeconds / 12d);
        return CommonInput(concatPath)
            .Concat([
                "-an",
                "-vf", $"fps=1/{interval.ToString("0.######", CultureInfo.InvariantCulture)},scale=240:136:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=240:136:(ow-iw)/2:(oh-ih)/2",
                "-q:v", "4",
                "-start_number", "0",
                "-progress", "pipe:1",
                "-nostats",
                outputPattern
            ])
            .ToArray();
    }

    public static IReadOnlyList<string> BuildWaveform(string concatPath, string outputPath)
    {
        return CommonInput(concatPath)
            .Concat([
                "-map", "0:a:0",
                "-vn",
                "-ac", "1",
                "-ar", "200",
                "-c:a", "pcm_s16le",
                "-f", "s16le",
                "-progress", "pipe:1",
                "-nostats",
                outputPath
            ])
            .ToArray();
    }

    private static IReadOnlyList<string> CommonInput(string concatPath) =>
    [
        "-y",
        "-hide_banner",
        "-loglevel", "error",
        "-f", "concat",
        "-safe", "0",
        "-i", concatPath
    ];
}
