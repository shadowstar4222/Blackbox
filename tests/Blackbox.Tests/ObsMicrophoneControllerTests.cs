using System.Text.Json.Nodes;
using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class ObsMicrophoneControllerTests
{
    [Fact]
    public async Task GetDevicesAsync_maps_obs_device_properties()
    {
        var rpc = new MicrophoneRpcClient();
        var controller = CreateController(rpc);

        var devices = await controller.GetDevicesAsync();

        var device = Assert.Single(devices);
        Assert.Equal("device-123", device.Id);
        Assert.Equal("USB microphone", device.Name);
        Assert.True(device.IsEnabled);
    }

    [Fact]
    public async Task ConfigureAsync_routes_one_device_to_both_paths_and_updates_the_processing_chain()
    {
        var rpc = new MicrophoneRpcClient();
        var controller = CreateController(rpc);

        await controller.ConfigureAsync(
            new MicrophoneDevice("device-123", "USB microphone"),
            new MicrophoneProcessingSettings
            {
                InputGainDb = 3,
                ExpanderThresholdDb = -40,
                CompressorThresholdDb = -16
            });

        Assert.Equal(2, rpc.BatchRequests.Count(request => request.RequestType == "SetInputSettings"));
        Assert.Equal(4, rpc.BatchRequests.Count(request => request.RequestType == "SetSourceFilterSettings"));
        Assert.Equal(5, rpc.BatchRequests.Count(request => request.RequestType == "SetSourceFilterEnabled"));
        Assert.All(
            rpc.BatchRequests.Where(request => request.RequestType == "SetInputSettings"),
            request => Assert.Equal("device-123", request.RequestData?["inputSettings"]?["device_id"]?.GetValue<string>()));
    }

    private static ObsMicrophoneController CreateController(MicrophoneRpcClient rpc) => new(
        rpc,
        new SilentAudioMeterClient(),
        new ObsConnectionSettingsProvider(),
        new FixedClock(DateTimeOffset.Parse("2026-07-15T12:00:00Z")));

    private sealed class MicrophoneRpcClient : IObsWebSocketRpcClient
    {
        public List<ObsRequest> BatchRequests { get; } = [];

        public Task<ObsConnectionStatus> TestConnectionAsync(
            ObsConnectionSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ObsConnectionStatus.Connected());

        public Task<ObsResponse> SendRequestAsync(
            ObsConnectionSettings settings,
            ObsRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ObsResponse.Successful(request.RequestType, new JsonObject
            {
                ["propertyItems"] = new JsonArray(new JsonObject
                {
                    ["itemName"] = "USB microphone",
                    ["itemValue"] = "device-123",
                    ["itemEnabled"] = true
                })
            }));

        public Task<IReadOnlyList<ObsResponse>> SendBatchAsync(
            ObsConnectionSettings settings,
            IReadOnlyList<ObsRequest> requests,
            CancellationToken cancellationToken = default)
        {
            BatchRequests.AddRange(requests);
            return Task.FromResult<IReadOnlyList<ObsResponse>>(
                requests.Select(request => ObsResponse.Successful(request.RequestType)).ToArray());
        }
    }

    private sealed class SilentAudioMeterClient : IObsAudioMeterClient
    {
        public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureInputLevelsAsync(
            ObsConnectionSettings settings,
            string inputName,
            TimeSpan duration,
            IProgress<AudioLevelSnapshot>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioLevelSnapshot>>([]);
    }
}
