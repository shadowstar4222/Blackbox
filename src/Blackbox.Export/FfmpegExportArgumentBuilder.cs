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
        usesStreamCopy = request.RangeStart <= TimeSpan.FromMilliseconds(10) &&
            request.Session.Duration - request.RangeEnd <= TimeSpan.FromMilliseconds(10);
        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            concatPath
        };
        if (!usesStreamCopy)
        {
            arguments.Add("-ss");
            arguments.Add(FormatTime(request.RangeStart));
            arguments.Add("-t");
            arguments.Add(FormatTime(request.RangeEnd - request.RangeStart));
        }

        arguments.Add("-map");
        arguments.Add("0:v:0");
        arguments.Add("-map");
        arguments.Add("0:a?");
        if (usesStreamCopy)
        {
            arguments.Add("-c");
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
            arguments.Add("-c:a");
            arguments.Add("aac");
            arguments.Add("-b:a");
            arguments.Add("192k");
        }

        AddAudioTitles(arguments, request.Session.Segments[0].AudioTrackLayout);
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

    private static void AddAudioTitles(List<string> arguments, string layout)
    {
        var titles = layout
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => value.Contains(':') ? value[(value.IndexOf(':') + 1)..] : value)
            .Select(NormalizeAudioTitle)
            .ToArray();
        for (var index = 0; index < titles.Length; index++)
        {
            arguments.Add($"-metadata:s:a:{index}");
            arguments.Add($"title={titles[index]}");
        }
    }

    private static string NormalizeAudioTitle(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "full_mix" => "Full listening mix",
            "game" => "Game audio",
            "voice" => "Voice chat",
            "raw_mic" => "Raw microphone",
            "processed_mic" => "Processed microphone",
            _ => value.Trim()
        };

    private static string FormatTime(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
}
