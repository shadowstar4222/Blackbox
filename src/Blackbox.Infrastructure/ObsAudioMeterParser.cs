using System.Text.Json.Nodes;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

internal static class ObsAudioMeterParser
{
    public static AudioLevelSnapshot? Parse(
        JsonObject payload,
        string inputName,
        DateTimeOffset capturedAt)
    {
        if (payload["op"]?.GetValue<int>() != 5 ||
            payload["d"]?["eventType"]?.GetValue<string>() != "InputVolumeMeters")
        {
            return null;
        }

        var input = payload["d"]?["eventData"]?["inputs"]?.AsArray()
            .FirstOrDefault(item => item?["inputName"]?.GetValue<string>() == inputName);
        var levels = input?["inputLevelsMul"]?.AsArray();
        if (levels is null || levels.Count == 0)
        {
            return null;
        }

        var magnitude = 0d;
        var peak = 0d;
        foreach (var levelNode in levels)
        {
            var channel = levelNode?.AsArray();
            if (channel is null || channel.Count < 2)
            {
                continue;
            }

            magnitude = Math.Max(magnitude, channel[0]?.GetValue<double>() ?? 0);
            peak = Math.Max(peak, channel[1]?.GetValue<double>() ?? 0);
        }

        return new AudioLevelSnapshot(inputName, LinearToDb(peak), LinearToDb(magnitude), capturedAt);
    }

    private static double LinearToDb(double value) =>
        value <= 0 ? double.NegativeInfinity : 20 * Math.Log10(value);
}
