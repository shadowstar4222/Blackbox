using System.Text.Json.Nodes;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsCaptureHealthClientTests
{
    [Fact]
    public async Task CaptureAsync_reads_exact_binding_preview_video_output_and_audio()
    {
        var target = Target();
        var rpc = new HealthRpcClient(BuildHealthyResponses(target));
        var audio = new AudioMeterClient(
            new AudioLevelSnapshot(
                "Blackbox Game Audio",
                -8.5,
                -14,
                DateTimeOffset.Parse("2026-07-25T12:00:00Z")));
        var client = CreateClient(rpc, audio);

        var result = await client.CaptureAsync(target, includePreview: true);

        Assert.True(result.SourceExists);
        Assert.True(result.SourceActive);
        Assert.True(result.SourceMatchesTarget);
        Assert.True(result.RecordingOutputActive);
        Assert.Equal(2560, result.OutputWidth);
        Assert.Equal(1440, result.OutputHeight);
        Assert.Equal(59.94, result.FramesPerSecond, 2);
        Assert.Equal(-8.5, result.GameAudioPeakDb);
        Assert.Equal(1, result.GameAudioSampleCount);
        Assert.Equal([1, 2, 3], result.Preview!.ImageBytes);
        Assert.Contains(rpc.LastRequests, request =>
            request.RequestType == "GetSourceScreenshot" &&
            request.RequestData?["imageWidth"]?.GetValue<int>() == 480 &&
            request.RequestData?["imageHeight"]?.GetValue<int>() == 270);
    }

    [Fact]
    public async Task CaptureAsync_detects_wrong_obs_window_policy()
    {
        var target = Target();
        var responses = BuildHealthyResponses(target).ToArray();
        responses[0] = ObsResponse.Successful("GetInputSettings", new JsonObject
        {
            ["inputSettings"] = new JsonObject
            {
                ["window"] = "Another Game:other.exe",
                ["capture_mode"] = "window",
                ["priority"] = 0
            }
        });
        var client = CreateClient(new HealthRpcClient(responses), new AudioMeterClient());

        var result = await client.CaptureAsync(target, includePreview: true);

        Assert.False(result.SourceMatchesTarget);
        Assert.Equal("Another Game:other.exe", result.BoundWindowIdentifier);
        Assert.Equal(0, result.WindowMatchPriority);
    }

    [Fact]
    public async Task CaptureAsync_returns_missing_source_evidence_without_losing_output_status()
    {
        var responses = BuildHealthyResponses(Target()).ToArray();
        responses[0] = new ObsResponse(
            "GetInputSettings",
            false,
            600,
            "No source was found",
            null);
        responses[1] = new ObsResponse(
            "GetSourceActive",
            false,
            600,
            "No source was found",
            null);
        responses[4] = new ObsResponse(
            "GetSourceScreenshot",
            false,
            600,
            "No source was found",
            null);
        var client = CreateClient(new HealthRpcClient(responses), new AudioMeterClient());

        var result = await client.CaptureAsync(Target(), includePreview: true);

        Assert.False(result.SourceExists);
        Assert.False(result.SourceActive);
        Assert.Null(result.Preview);
        Assert.True(result.RecordingOutputActive);
        Assert.Contains("No source was found", result.Evidence);
    }

    private static ObsCaptureHealthClient CreateClient(
        HealthRpcClient rpc,
        AudioMeterClient audio) => new(
            rpc,
            audio,
            new ConnectionSettingsProvider(),
            new FixedClock(DateTimeOffset.Parse("2026-07-25T12:00:00Z")),
            NullLogger<ObsCaptureHealthClient>.Instance);

    private static IReadOnlyList<ObsResponse> BuildHealthyResponses(GameCaptureTarget target) =>
    [
        ObsResponse.Successful("GetInputSettings", new JsonObject
        {
            ["inputKind"] = "game_capture",
            ["inputSettings"] = new JsonObject
            {
                ["window"] = target.ObsWindowIdentifier,
                ["capture_mode"] = ObsGameCaptureSettings.SpecificWindowMode,
                ["priority"] = ObsGameCaptureSettings.WindowTitleMustMatchPriority
            }
        }),
        ObsResponse.Successful("GetSourceActive", new JsonObject
        {
            ["videoActive"] = true,
            ["videoShowing"] = true
        }),
        ObsResponse.Successful("GetVideoSettings", new JsonObject
        {
            ["fpsNumerator"] = 60000,
            ["fpsDenominator"] = 1001,
            ["outputWidth"] = 2560,
            ["outputHeight"] = 1440
        }),
        ObsResponse.Successful("GetRecordStatus", new JsonObject
        {
            ["outputActive"] = true
        }),
        ObsResponse.Successful("GetSourceScreenshot", new JsonObject
        {
            ["imageData"] = "data:image/png;base64,AQID"
        })
    ];

    private static GameCaptureTarget Target() => new(
        42,
        @"C:\Games\Example\game.exe",
        "game",
        "Example Game",
        "Example Game:game.exe",
        GameDetectionSource.ForegroundWindow);

    private sealed class ConnectionSettingsProvider : IObsConnectionSettingsProvider
    {
        public ObsConnectionSettings Current { get; private set; } = new()
        {
            Host = "127.0.0.1",
            Port = 4455,
            Password = "password"
        };
        public void Set(ObsConnectionSettings settings) => Current = settings;
    }

    private sealed class AudioMeterClient(params AudioLevelSnapshot[] snapshots) : IObsAudioMeterClient
    {
        public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureInputLevelsAsync(
            ObsConnectionSettings settings,
            string inputName,
            TimeSpan duration,
            IProgress<AudioLevelSnapshot>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioLevelSnapshot>>(snapshots);
    }

    private sealed class HealthRpcClient(IReadOnlyList<ObsResponse> responses) : IObsWebSocketRpcClient
    {
        public IReadOnlyList<ObsRequest> LastRequests { get; private set; } = [];

        public Task<ObsConnectionStatus> TestConnectionAsync(
            ObsConnectionSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ObsConnectionStatus.Connected("Connected"));

        public Task<ObsResponse> SendRequestAsync(
            ObsConnectionSettings settings,
            ObsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ObsResponse>> SendBatchAsync(
            ObsConnectionSettings settings,
            IReadOnlyList<ObsRequest> requests,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(responses);

        public Task<IReadOnlyList<ObsResponse>> SendBatchAllowFailuresAsync(
            ObsConnectionSettings settings,
            IReadOnlyList<ObsRequest> requests,
            CancellationToken cancellationToken = default)
        {
            LastRequests = requests;
            return Task.FromResult(responses);
        }
    }
}
