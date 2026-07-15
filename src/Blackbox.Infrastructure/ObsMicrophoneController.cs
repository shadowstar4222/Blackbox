using System.Text.Json.Nodes;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class ObsMicrophoneController(
    IObsWebSocketRpcClient rpcClient,
    IObsAudioMeterClient audioMeterClient,
    IObsConnectionSettingsProvider connectionSettingsProvider,
    IClock clock) : IObsMicrophoneController
{
    public const string RawMicrophoneSourceName = "Blackbox Raw Microphone";
    public const string ProcessedMicrophoneSourceName = "Blackbox Processed Microphone";

    public async Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await rpcClient.SendRequestAsync(
            connectionSettingsProvider.Current,
            new ObsRequest("GetInputPropertiesListPropertyItems", new JsonObject
            {
                ["inputName"] = RawMicrophoneSourceName,
                ["propertyName"] = "device_id"
            }),
            cancellationToken);
        return response.ResponseData?["propertyItems"]?.AsArray()
            .Select(static item => new MicrophoneDevice(
                item?["itemValue"]?.GetValue<string>() ?? string.Empty,
                item?["itemName"]?.GetValue<string>() ?? "Unknown microphone",
                item?["itemEnabled"]?.GetValue<bool>() ?? true))
            .Where(static device => device.Id.Length > 0)
            .ToArray()
            ?? [];
    }

    public async Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A microphone device is required.", nameof(deviceId));
        }

        var devices = await GetDevicesAsync(cancellationToken);
        var connected = devices.Any(device =>
            device.IsEnabled && device.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        return new MicrophoneDeviceStatus(
            deviceId,
            connected ? MicrophoneConnectionState.Connected : MicrophoneConnectionState.Disconnected,
            clock.UtcNow);
    }

    public async Task ConfigureAsync(
        MicrophoneDevice device,
        MicrophoneProcessingSettings processingSettings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(device.Id))
        {
            throw new ArgumentException("A microphone device is required.", nameof(device));
        }

        processingSettings.Validate();
        var requests = new List<ObsRequest>
        {
            InputSettings(RawMicrophoneSourceName, device.Id),
            InputSettings(ProcessedMicrophoneSourceName, device.Id),
            FilterSettings("Blackbox Input Gain", new JsonObject { ["db"] = processingSettings.InputGainDb }),
            FilterSettings("Blackbox Expander", new JsonObject { ["threshold_db"] = processingSettings.ExpanderThresholdDb }),
            FilterSettings("Blackbox Compressor", new JsonObject
            {
                ["threshold_db"] = processingSettings.CompressorThresholdDb,
                ["ratio"] = processingSettings.CompressorRatio
            }),
            FilterSettings("Blackbox Limiter", new JsonObject { ["threshold_db"] = processingSettings.LimiterThresholdDb }),
            FilterEnabled("Blackbox Input Gain", true),
            FilterEnabled("Blackbox Noise Suppression", processingSettings.NoiseSuppressionEnabled),
            FilterEnabled("Blackbox Expander", processingSettings.ExpanderEnabled),
            FilterEnabled("Blackbox Compressor", processingSettings.CompressorEnabled),
            FilterEnabled("Blackbox Limiter", processingSettings.LimiterEnabled)
        };
        await rpcClient.SendBatchAsync(connectionSettingsProvider.Current, requests, cancellationToken);
    }

    public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(
        TimeSpan duration,
        IProgress<AudioLevelSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        audioMeterClient.CaptureInputLevelsAsync(
            connectionSettingsProvider.Current,
            RawMicrophoneSourceName,
            duration,
            progress,
            cancellationToken);

    public async Task SetProcessingEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var requests = new[]
        {
            "Blackbox Input Gain",
            "Blackbox Noise Suppression",
            "Blackbox Expander",
            "Blackbox Compressor",
            "Blackbox Limiter"
        }.Select(name => FilterEnabled(name, enabled)).ToArray();
        await rpcClient.SendBatchAsync(connectionSettingsProvider.Current, requests, cancellationToken);
    }

    public async Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default)
    {
        var response = await rpcClient.SendRequestAsync(
            connectionSettingsProvider.Current,
            new ObsRequest("GetRecordStatus"),
            cancellationToken);
        return response.ResponseData?["outputActive"]?.GetValue<bool>() ?? false;
    }

    private static ObsRequest InputSettings(string inputName, string deviceId) =>
        new("SetInputSettings", new JsonObject
        {
            ["inputName"] = inputName,
            ["inputSettings"] = new JsonObject { ["device_id"] = deviceId },
            ["overlay"] = true
        });

    private static ObsRequest FilterSettings(string filterName, JsonObject settings) =>
        new("SetSourceFilterSettings", new JsonObject
        {
            ["sourceName"] = ProcessedMicrophoneSourceName,
            ["filterName"] = filterName,
            ["filterSettings"] = settings,
            ["overlay"] = true
        });

    private static ObsRequest FilterEnabled(string filterName, bool enabled) =>
        new("SetSourceFilterEnabled", new JsonObject
        {
            ["sourceName"] = ProcessedMicrophoneSourceName,
            ["filterName"] = filterName,
            ["filterEnabled"] = enabled
        });
}
