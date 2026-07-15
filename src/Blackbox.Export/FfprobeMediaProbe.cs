using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class FfprobeMediaProbe(
    IFfmpegProvisioner provisioner,
    ILogger<FfprobeMediaProbe> logger) : IMediaProbe
{
    public async Task<MediaFileProbeResult> ProbeAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The recording file could not be found.", filePath);
        }

        var installation = await provisioner.EnsureInstalledAsync(cancellationToken: cancellationToken);
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.FfprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(filePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start ffprobe.");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"ffprobe could not read {Path.GetFileName(filePath)}: {error.Trim()}");
        }

        var result = FfprobeOutputParser.Parse(output);
        logger.LogDebug(
            "Probed recording {RecordingPath}. Duration={Duration}, Video={VideoCodec}, AudioTracks={AudioTrackCount}.",
            filePath,
            result.Duration,
            result.VideoCodec,
            result.AudioTrackTitles.Count);
        return result;
    }
}

internal static class FfprobeOutputParser
{
    private static readonly string[] DefaultAudioTitles =
    [
        "Full listening mix",
        "Game audio",
        "Voice chat",
        "Raw microphone",
        "Processed microphone"
    ];

    public static MediaFileProbeResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(static stream =>
            stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("The recording does not contain a video stream.");
        }

        var durationSeconds = ReadDouble(root.GetProperty("format"), "duration");
        if (durationSeconds <= 0)
        {
            durationSeconds = streams.Max(static stream => ReadDouble(stream, "duration"));
        }

        if (durationSeconds <= 0)
        {
            throw new InvalidDataException("The recording duration could not be determined.");
        }

        var audioTitles = streams
            .Where(static stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio")
            .Select((stream, index) => ReadAudioTitle(stream, index))
            .ToArray();
        var transfer = ReadString(video, "color_transfer");
        return new MediaFileProbeResult(
            TimeSpan.FromSeconds(durationSeconds),
            ReadString(video, "codec_name", "unknown"),
            ReadString(video, "pix_fmt", "unknown"),
            ReadInt32(video, "width"),
            ReadInt32(video, "height"),
            ReadFrameRate(video),
            transfer is "smpte2084" or "arib-std-b67",
            audioTitles);
    }

    private static string ReadAudioTitle(JsonElement stream, int index)
    {
        if (stream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("title", out var title) &&
            !string.IsNullOrWhiteSpace(title.GetString()))
        {
            return title.GetString()!;
        }

        return index < DefaultAudioTitles.Length
            ? DefaultAudioTitles[index]
            : $"Audio track {index + 1}";
    }

    private static decimal ReadFrameRate(JsonElement video)
    {
        var value = ReadString(video, "avg_frame_rate", "0/1");
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }

        return 0;
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static int ReadInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static string ReadString(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
}
