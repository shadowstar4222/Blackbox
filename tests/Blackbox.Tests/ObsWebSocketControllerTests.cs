using System.Text.Json.Nodes;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsWebSocketControllerTests
{
    [Fact]
    public async Task ApplySetupPlanAsync_creates_missing_resources_and_configures_recording()
    {
        var rpc = new RecordingRpcClient(resourcesExist: false);
        var controller = CreateController(rpc);
        var plan = new ObsSetupPlanner().CreateDefaultPlan(new RecordingSettings { RecordingLocation = "C:\\Recordings" });

        await controller.ApplySetupPlanAsync(new ObsConnectionSettings(), plan);

        var requests = rpc.AllRequests.ToArray();
        Assert.Contains(requests, static request => request.RequestType == "CreateProfile");
        Assert.Contains(requests, static request => request.RequestType == "CreateSceneCollection");
        Assert.Contains(requests, static request => request.RequestType == "CreateScene");
        Assert.Equal(5, requests.Count(static request => request.RequestType == "CreateInput"));
        Assert.Equal(5, requests.Count(static request => request.RequestType == "CreateSourceFilter"));
        Assert.Contains(requests, static request => request.RequestType == "SetProfileParameter");
        Assert.Equal(2, requests.Count(static request => request.RequestType == "SetCurrentProfile"));
    }

    [Fact]
    public async Task ApplySetupPlanAsync_reuses_existing_resources()
    {
        var rpc = new RecordingRpcClient(resourcesExist: true);
        var controller = CreateController(rpc);
        var plan = new ObsSetupPlanner().CreateDefaultPlan(new RecordingSettings { RecordingLocation = "C:\\Recordings" });

        await controller.ApplySetupPlanAsync(new ObsConnectionSettings(), plan);

        var requests = rpc.AllRequests.ToArray();
        Assert.DoesNotContain(requests, static request =>
            request.RequestType == "CreateProfile" &&
            request.RequestData?["profileName"]?.GetValue<string>() == "Blackbox");
        Assert.Contains(requests, static request => request.RequestType == "RemoveProfile");
        Assert.DoesNotContain(requests, static request => request.RequestType == "CreateSceneCollection");
        Assert.DoesNotContain(requests, static request => request.RequestType == "CreateScene");
        Assert.DoesNotContain(requests, static request => request.RequestType == "CreateInput");
        Assert.DoesNotContain(requests, static request => request.RequestType == "CreateSourceFilter");
    }

    [Fact]
    public async Task StartRecordingAsync_sends_start_record_request()
    {
        var rpc = new RecordingRpcClient(resourcesExist: true);
        var controller = CreateController(rpc);

        await controller.StartRecordingAsync();

        Assert.Equal("StartRecord", rpc.AllRequests.Single().RequestType);
    }

    [Fact]
    public async Task ConfigureGameCaptureAsync_updates_video_and_audio_inputs()
    {
        var rpc = new RecordingRpcClient(resourcesExist: true);
        var controller = CreateController(rpc);
        var target = new GameCaptureTarget(
            42,
            "C:\\Steam\\steamapps\\common\\Example\\Example.exe",
            "Example.exe",
            "Example Game",
            "Example Game:ExampleWindow:Example.exe",
            GameDetectionSource.ForegroundWindow | GameDetectionSource.SteamLibrary);

        await controller.ConfigureGameCaptureAsync(target);

        Assert.Equal(2, rpc.AllRequests.Count(static request => request.RequestType == "SetInputSettings"));
        Assert.Contains(rpc.AllRequests, static request => request.RequestType == "SetVideoSettings");
        Assert.Contains(rpc.AllRequests, static request => request.RequestType == "SetSceneItemTransform");
        Assert.Equal(4, rpc.AllRequests.Count(static request => request.RequestType == "SetSceneItemEnabled"));
        Assert.Contains(rpc.AllRequests, static request =>
            request.RequestType == "SetCurrentProgramScene" &&
            request.RequestData?["sceneName"]?.GetValue<string>() == "Blackbox Recording");
    }

    private static ObsWebSocketController CreateController(IObsWebSocketRpcClient rpc) =>
        new(
            rpc,
            new ObsConnectionSettingsProvider(),
            new ObsSetupRequestBuilder(),
            NullLogger<ObsWebSocketController>.Instance);

    private sealed class RecordingRpcClient(bool resourcesExist) : IObsWebSocketRpcClient
    {
        private static readonly string[] InputKinds =
        [
            "game_capture",
            "wasapi_process_output_capture",
            "wasapi_input_capture"
        ];

        private static readonly string[] FilterKinds =
        [
            "gain_filter",
            "noise_suppress_filter_v2",
            "expander_filter",
            "compressor_filter",
            "limiter_filter"
        ];

        private readonly List<ObsRequest> _requests = [];

        public IEnumerable<ObsRequest> AllRequests => _requests;

        public Task<ObsConnectionStatus> TestConnectionAsync(
            ObsConnectionSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ObsConnectionStatus.Connected());

        public Task<ObsResponse> SendRequestAsync(
            ObsConnectionSettings settings,
            ObsRequest request,
            CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            return Task.FromResult(ResponseFor(request));
        }

        public Task<IReadOnlyList<ObsResponse>> SendBatchAsync(
            ObsConnectionSettings settings,
            IReadOnlyList<ObsRequest> requests,
            CancellationToken cancellationToken = default)
        {
            _requests.AddRange(requests);
            IReadOnlyList<ObsResponse> responses = requests.Select(ResponseFor).ToArray();
            return Task.FromResult(responses);
        }

        private ObsResponse ResponseFor(ObsRequest request)
        {
            return request.RequestType switch
            {
                "GetProfileList" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["currentProfileName"] = resourcesExist ? "Blackbox" : "Default",
                    ["profiles"] = StringArray(resourcesExist ? ["Blackbox"] : ["Default"])
                }),
                "GetSceneCollectionList" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["currentSceneCollectionName"] = resourcesExist ? "Blackbox" : "Untitled",
                    ["sceneCollections"] = StringArray(resourcesExist ? ["Blackbox"] : ["Untitled"])
                }),
                "GetSceneList" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["scenes"] = resourcesExist
                        ? new JsonArray(new JsonObject { ["sceneName"] = "Blackbox Recording" })
                        : new JsonArray()
                }),
                "GetInputList" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["inputs"] = resourcesExist
                        ? new JsonArray(
                            new JsonObject { ["inputName"] = "Blackbox Game Capture" },
                            new JsonObject { ["inputName"] = "Blackbox Game Audio" },
                            new JsonObject { ["inputName"] = "Blackbox Voice Chat" },
                            new JsonObject { ["inputName"] = "Blackbox Raw Microphone" },
                            new JsonObject { ["inputName"] = "Blackbox Processed Microphone" })
                        : new JsonArray()
                }),
                "GetSourceFilterList" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["filters"] = resourcesExist
                        ? new JsonArray(
                            new JsonObject { ["filterName"] = "Blackbox Input Gain" },
                            new JsonObject { ["filterName"] = "Blackbox Noise Suppression" },
                            new JsonObject { ["filterName"] = "Blackbox Expander" },
                            new JsonObject { ["filterName"] = "Blackbox Compressor" },
                            new JsonObject { ["filterName"] = "Blackbox Limiter" })
                        : new JsonArray()
                }),
                "GetInputKindList" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["inputKinds"] = StringArray(InputKinds)
                }),
                "GetSourceFilterKindList" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["sourceFilterKinds"] = StringArray(FilterKinds)
                }),
                "GetSceneItemId" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["sceneItemId"] = request.RequestData?["sourceName"]?.GetValue<string>() == "Blackbox Game Capture"
                        ? 100
                        : 101
                }),
                "StopRecord" => ObsResponse.Successful(request.RequestType, new JsonObject
                {
                    ["outputPath"] = "C:\\Recordings\\probe.mkv"
                }),
                _ => ObsResponse.Successful(request.RequestType)
            };
        }

        private static JsonArray StringArray(IEnumerable<string> values) =>
            new(values.Select(static value => JsonValue.Create(value)).ToArray());
    }
}
