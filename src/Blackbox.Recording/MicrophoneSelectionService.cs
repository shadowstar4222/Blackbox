using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class MicrophoneSelectionService(
    IObsMicrophoneController microphoneController,
    IDefaultMicrophoneProvider defaultMicrophoneProvider,
    IMicrophoneConfigurationStore configurationStore,
    ILogger<MicrophoneSelectionService> logger)
{
    public async Task<MicrophoneDevice> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var configuration = configurationStore.Current;
        var devices = (await microphoneController.GetDevicesAsync(cancellationToken))
            .Where(static device => device.IsEnabled)
            .ToArray();
        if (devices.Length == 0)
        {
            throw new InvalidOperationException("OBS did not report any available microphones.");
        }

        MicrophoneDevice? selected;
        if (!configuration.AutomaticallySelectDevice)
        {
            selected = FindDevice(devices, configuration.DeviceId)
                ?? throw new InvalidOperationException(
                    $"The selected microphone '{configuration.DeviceName}' is not available.");
        }
        else
        {
            var excluded = configuration.ExcludedDeviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var windowsDefaultId = defaultMicrophoneProvider.GetDefaultDeviceId();
            selected = FindAllowedDevice(devices, windowsDefaultId, excluded)
                ?? FindAllowedDevice(devices, configuration.DeviceId, excluded)
                ?? devices.FirstOrDefault(device =>
                    !device.Id.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                    !excluded.Contains(device.Id));

            var defaultIsExcluded = !string.IsNullOrWhiteSpace(windowsDefaultId) &&
                excluded.Contains(windowsDefaultId);
            selected ??= defaultIsExcluded
                ? null
                : FindAllowedDevice(devices, "default", excluded);
            if (selected is null)
            {
                throw new InvalidOperationException(
                    "Every available microphone is excluded from automatic routing.");
            }
        }

        if (!configuration.DeviceId.Equals(selected.Id, StringComparison.OrdinalIgnoreCase) ||
            !configuration.DeviceName.Equals(selected.Name, StringComparison.Ordinal))
        {
            configurationStore.Save(configuration with
            {
                DeviceId = selected.Id,
                DeviceName = selected.Name
            });
            logger.LogInformation("Selected microphone {MicrophoneName} for Blackbox audio routing.", selected.Name);
        }

        return selected;
    }

    public async Task<MicrophoneDevice> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var selected = await ResolveAsync(cancellationToken);
        await microphoneController.ConfigureAsync(
            selected,
            configurationStore.Current.ProcessingSettings,
            cancellationToken);
        return selected;
    }

    private static MicrophoneDevice? FindAllowedDevice(
        IReadOnlyList<MicrophoneDevice> devices,
        string? deviceId,
        HashSet<string> excluded) =>
        string.IsNullOrWhiteSpace(deviceId) || excluded.Contains(deviceId)
            ? null
            : FindDevice(devices, deviceId);

    private static MicrophoneDevice? FindDevice(
        IReadOnlyList<MicrophoneDevice> devices,
        string deviceId) =>
        devices.FirstOrDefault(device =>
            device.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
}
