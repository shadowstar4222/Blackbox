using System.Text.Json.Nodes;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class ObsCaptureHealthClient(
    IObsWebSocketRpcClient rpcClient,
    IObsAudioMeterClient audioMeterClient,
    IObsConnectionSettingsProvider connectionSettingsProvider,
    IClock clock,
    ILogger<ObsCaptureHealthClient> logger) : IObsCaptureHealthClient
{
    private const string GameCaptureSource = "Blackbox Game Capture";
    private const string GameAudioSource = "Blackbox Game Audio";
    private static readonly TimeSpan AudioSampleDuration = TimeSpan.FromMilliseconds(300);

    public async Task<ObsCaptureHealthSample> CaptureAsync(
        GameCaptureTarget? target,
        bool includePreview,
        CancellationToken cancellationToken = default)
    {
        var capturedAt = clock.UtcNow;
        var requests = new List<ObsRequest>
        {
            new("GetInputSettings", new JsonObject { ["inputName"] = GameCaptureSource }),
            new("GetSourceActive", new JsonObject { ["sourceName"] = GameCaptureSource }),
            new("GetVideoSettings"),
            new("GetRecordStatus")
        };
        if (includePreview)
        {
            requests.Add(new ObsRequest("GetSourceScreenshot", new JsonObject
            {
                ["sourceName"] = GameCaptureSource,
                ["imageFormat"] = "png",
                ["imageWidth"] = 480,
                ["imageHeight"] = 270,
                ["imageCompressionQuality"] = 60
            }));
        }

        var responses = await rpcClient.SendBatchAllowFailuresAsync(
            connectionSettingsProvider.Current,
            requests,
            cancellationToken);
        var inputResponse = Find(responses, "GetInputSettings");
        var activeResponse = Find(responses, "GetSourceActive");
        var videoResponse = Find(responses, "GetVideoSettings");
        var recordResponse = Find(responses, "GetRecordStatus");
        var screenshotResponse = includePreview ? Find(responses, "GetSourceScreenshot") : null;

        var sourceExists = inputResponse?.IsSuccessful == true;
        var inputSettings = inputResponse?.ResponseData?["inputSettings"]?.AsObject();
        var boundWindow = inputSettings?["window"]?.GetValue<string>();
        var captureMode = inputSettings?["capture_mode"]?.GetValue<string>();
        var priority = inputSettings?["priority"]?.GetValue<int>();
        var sourceMatchesTarget = target is null ||
            string.Equals(boundWindow, target.ObsWindowIdentifier, StringComparison.Ordinal) &&
            string.Equals(captureMode, ObsGameCaptureSettings.SpecificWindowMode, StringComparison.Ordinal) &&
            priority == ObsGameCaptureSettings.WindowTitleMustMatchPriority;
        var sourceActive = activeResponse?.IsSuccessful == true &&
            activeResponse.ResponseData?["videoActive"]?.GetValue<bool>() == true;

        var numerator = GetDouble(videoResponse?.ResponseData?["fpsNumerator"]);
        var denominator = GetDouble(videoResponse?.ResponseData?["fpsDenominator"]);
        var framesPerSecond = numerator > 0 && denominator > 0 ? numerator / denominator : 0;
        var preview = screenshotResponse?.IsSuccessful == true
            ? ParsePreview(screenshotResponse.ResponseData?["imageData"]?.GetValue<string>(), capturedAt)
            : null;

        IReadOnlyList<AudioLevelSnapshot> audioLevels = [];
        if (sourceExists && target?.CaptureGameAudio != false)
        {
            try
            {
                audioLevels = await audioMeterClient.CaptureInputLevelsAsync(
                    connectionSettingsProvider.Current,
                    GameAudioSource,
                    AudioSampleDuration,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "OBS game-audio health sample was unavailable.");
            }
        }

        var evidence = BuildFailureEvidence(inputResponse, activeResponse, screenshotResponse);
        return new ObsCaptureHealthSample(
            capturedAt,
            sourceExists,
            sourceActive,
            sourceMatchesTarget,
            boundWindow,
            captureMode,
            priority,
            GetInt(videoResponse?.ResponseData?["outputWidth"]),
            GetInt(videoResponse?.ResponseData?["outputHeight"]),
            framesPerSecond,
            recordResponse?.IsSuccessful == true &&
                recordResponse.ResponseData?["outputActive"]?.GetValue<bool>() == true,
            audioLevels.Count == 0 ? null : audioLevels.Max(static level => level.PeakDb),
            audioLevels.Count,
            preview,
            evidence);
    }

    private static ObsResponse? Find(IEnumerable<ObsResponse> responses, string requestType) =>
        responses.FirstOrDefault(response => response.RequestType == requestType);

    private static ObsPreviewFrame? ParsePreview(string? imageData, DateTimeOffset capturedAt)
    {
        if (string.IsNullOrWhiteSpace(imageData))
        {
            return null;
        }

        var separator = imageData.IndexOf(',');
        if (separator <= 5 || !imageData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var descriptor = imageData[5..separator];
        var mediaType = descriptor.Split(';', 2)[0];
        try
        {
            return new ObsPreviewFrame(
                Convert.FromBase64String(imageData[(separator + 1)..]),
                mediaType,
                capturedAt);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? BuildFailureEvidence(params ObsResponse?[] responses)
    {
        var failures = responses
            .Where(static response => response is { IsSuccessful: false })
            .Select(static response =>
                $"{response!.RequestType}: {response.Comment ?? $"OBS code {response.Code}"}")
            .ToArray();
        return failures.Length == 0 ? null : string.Join("; ", failures);
    }

    private static int GetInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        return value.TryGetValue<long>(out var longValue) &&
               longValue is >= int.MinValue and <= int.MaxValue
            ? (int)longValue
            : 0;
    }

    private static double GetDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        return value.TryGetValue<int>(out var intValue) ? intValue : 0;
    }
}
