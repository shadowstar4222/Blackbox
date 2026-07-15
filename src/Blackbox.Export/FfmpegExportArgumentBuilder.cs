using System.Globalization;
using Blackbox.Domain;

namespace Blackbox.Export;

internal static class FfmpegExportArgumentBuilder
{
    public static IReadOnlyList<string> Build(
        SessionExportRequest request,
        string concatPath,
        string temporaryOutputPath,
        out bool usesStreamCopy)
    {
        var isFullRange = request.RangeStart <= TimeSpan.FromMilliseconds(10) &&
            request.Session.Duration - request.RangeEnd <= TimeSpan.FromMilliseconds(10);
        var configuredTracks = AudioTrackSelectionResolver.Resolve(request);
        var audibleTracks = AudioTrackSelectionResolver.Audible(configuredTracks);
        var audioIsUnchanged = AudioTrackSelectionResolver.IsDefault(request, configuredTracks);
        usesStreamCopy = isFullRange && audioIsUnchanged;
        var arguments = CommonInput(concatPath);
        if (!isFullRange)
        {
            arguments.Add("-ss");
            arguments.Add(FormatTime(request.RangeStart));
            arguments.Add("-t");
            arguments.Add(FormatTime(request.RangeEnd - request.RangeStart));
        }

        arguments.Add("-map");
        arguments.Add("0:v:0");
        foreach (var track in audibleTracks)
        {
            arguments.Add("-map");
            arguments.Add($"0:a:{track.StreamIndex}?");
        }

        if (isFullRange)
        {
            arguments.Add("-c:v");
            arguments.Add("copy");
        }
        else
        {
            arguments.Add("-c:v");
            arguments.Add("libx264");
            arguments.Add("-preset");
            arguments.Add("veryfast");
            arguments.Add("-crf");
            arguments.Add("18");
        }

        if (audibleTracks.Count == 0)
        {
            arguments.Add("-an");
        }
        else if (usesStreamCopy)
        {
            arguments.Add("-c:a");
            arguments.Add("copy");
        }
        else
        {
            arguments.Add("-c:a");
            arguments.Add("aac");
            arguments.Add("-b:a");
            arguments.Add("192k");
        }

        for (var outputIndex = 0; outputIndex < audibleTracks.Count; outputIndex++)
        {
            var track = audibleTracks[outputIndex];
            if (Math.Abs(track.Volume - 1) >= 0.0001)
            {
                arguments.Add($"-filter:a:{outputIndex}");
                arguments.Add($"volume={track.Volume.ToString("0.###", CultureInfo.InvariantCulture)}");
            }

            arguments.Add($"-metadata:s:a:{outputIndex}");
            arguments.Add($"title={track.Name}");
        }

        arguments.Add("-avoid_negative_ts");
        arguments.Add("make_zero");
        arguments.Add("-max_muxing_queue_size");
        arguments.Add("4096");
        if (Path.GetExtension(request.DestinationPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-movflags");
            arguments.Add("+faststart");
        }

        arguments.Add("-progress");
        arguments.Add("pipe:1");
        arguments.Add("-nostats");
        arguments.Add(temporaryOutputPath);
        return arguments;
    }

    internal static List<string> CommonInput(string concatPath) =>
    [
        "-y",
        "-hide_banner",
        "-loglevel", "error",
        "-f", "concat",
        "-safe", "0",
        "-i", concatPath
    ];

    internal static string FormatTime(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
}
