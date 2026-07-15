using System.Globalization;
using Blackbox.Domain;

namespace Blackbox.Export;

internal static class FfmpegAudioExportArgumentBuilder
{
    public static IReadOnlyList<string> Build(
        SessionExportRequest request,
        AudioTrackExportSelection track,
        string concatPath,
        string outputPath)
    {
        var arguments = FfmpegExportArgumentBuilder.CommonInput(concatPath);
        var isFullRange = request.RangeStart <= TimeSpan.FromMilliseconds(10) &&
            request.Session.Duration - request.RangeEnd <= TimeSpan.FromMilliseconds(10);
        if (!isFullRange)
        {
            arguments.Add("-ss");
            arguments.Add(FfmpegExportArgumentBuilder.FormatTime(request.RangeStart));
            arguments.Add("-t");
            arguments.Add(FfmpegExportArgumentBuilder.FormatTime(request.RangeEnd - request.RangeStart));
        }

        arguments.Add("-map");
        arguments.Add($"0:a:{track.StreamIndex}");
        arguments.Add("-vn");
        arguments.Add("-c:a");
        arguments.Add("pcm_s24le");
        if (Math.Abs(track.Volume - 1) >= 0.0001)
        {
            arguments.Add("-af");
            arguments.Add($"volume={track.Volume.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        arguments.Add("-progress");
        arguments.Add("pipe:1");
        arguments.Add("-nostats");
        arguments.Add(outputPath);
        return arguments;
    }
}
