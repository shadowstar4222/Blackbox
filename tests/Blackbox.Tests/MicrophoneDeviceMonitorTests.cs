using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class MicrophoneDeviceMonitorTests
{
    [Fact]
    public async Task Monitor_reapplies_saved_configuration_when_the_selected_device_reconnects()
    {
        var controller = new ReconnectingMicrophoneController();
        var store = new InMemoryConfigurationStore(new MicrophoneConfiguration
        {
            DeviceId = "device-123",
            DeviceName = "USB microphone",
            ProcessingSettings = new MicrophoneProcessingSettings { InputGainDb = 2.5 }
        });
        var monitor = new MicrophoneDeviceMonitor(
            controller,
            store,
            new FixedClock(DateTimeOffset.Parse("2026-07-15T12:00:00Z")),
            new MicrophoneMonitoringOptions { PollInterval = TimeSpan.FromMilliseconds(1) },
            NullLogger<MicrophoneDeviceMonitor>.Instance);

        await monitor.StartAsync();
        await controller.Reconfigured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync();

        Assert.Equal("device-123", controller.ConfiguredDevice?.Id);
        Assert.Equal(2.5, controller.ConfiguredSettings?.InputGainDb);
        Assert.Equal(MicrophoneConnectionState.Connected, monitor.CurrentStatus.State);
    }

    [Fact]
    public async Task Monitor_reroutes_when_the_Windows_default_microphone_changes()
    {
        var controller = new SwitchingMicrophoneController();
        var store = new InMemoryConfigurationStore(new MicrophoneConfiguration
        {
            DeviceId = "old-device",
            DeviceName = "Old microphone"
        });
        var selection = new MicrophoneSelectionService(
            controller,
            new FixedDefaultMicrophoneProvider("new-device"),
            store,
            NullLogger<MicrophoneSelectionService>.Instance);
        var monitor = new MicrophoneDeviceMonitor(
            controller,
            store,
            new FixedClock(DateTimeOffset.Parse("2026-07-15T12:00:00Z")),
            new MicrophoneMonitoringOptions { PollInterval = TimeSpan.FromMilliseconds(1) },
            NullLogger<MicrophoneDeviceMonitor>.Instance,
            selection);

        await monitor.StartAsync();
        await controller.Reconfigured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync();

        Assert.Equal("new-device", controller.ConfiguredDevice?.Id);
        Assert.Equal("new-device", store.Current.DeviceId);
    }

    private sealed class InMemoryConfigurationStore(MicrophoneConfiguration configuration)
        : IMicrophoneConfigurationStore
    {
        public MicrophoneConfiguration Current { get; private set; } = configuration;

        public void Save(MicrophoneConfiguration updatedConfiguration) => Current = updatedConfiguration;
    }

    private sealed class ReconnectingMicrophoneController : IObsMicrophoneController
    {
        private int _statusChecks;

        public TaskCompletionSource Reconfigured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MicrophoneDevice? ConfiguredDevice { get; private set; }
        public MicrophoneProcessingSettings? ConfiguredSettings { get; private set; }

        public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            var state = Interlocked.Increment(ref _statusChecks) == 1
                ? MicrophoneConnectionState.Disconnected
                : MicrophoneConnectionState.Connected;
            return Task.FromResult(new MicrophoneDeviceStatus(deviceId, state, DateTimeOffset.UtcNow));
        }

        public Task ConfigureAsync(
            MicrophoneDevice device,
            MicrophoneProcessingSettings processingSettings,
            CancellationToken cancellationToken = default)
        {
            ConfiguredDevice = device;
            ConfiguredSettings = processingSettings;
            Reconfigured.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(
            TimeSpan duration,
            IProgress<AudioLevelSnapshot>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetProcessingEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedDefaultMicrophoneProvider(string deviceId) : IDefaultMicrophoneProvider
    {
        public string? GetDefaultDeviceId() => deviceId;
    }

    private sealed class SwitchingMicrophoneController : IObsMicrophoneController
    {
        public TaskCompletionSource Reconfigured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MicrophoneDevice? ConfiguredDevice { get; private set; }

        public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MicrophoneDevice>>(
            [
                new("default", "Default"),
                new("old-device", "Old microphone"),
                new("new-device", "New microphone")
            ]);

        public Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(
            string deviceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MicrophoneDeviceStatus(
                deviceId,
                MicrophoneConnectionState.Connected,
                DateTimeOffset.UtcNow));

        public Task ConfigureAsync(
            MicrophoneDevice device,
            MicrophoneProcessingSettings processingSettings,
            CancellationToken cancellationToken = default)
        {
            ConfiguredDevice = device;
            Reconfigured.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(
            TimeSpan duration,
            IProgress<AudioLevelSnapshot>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetProcessingEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
