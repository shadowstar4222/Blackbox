using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class MicrophoneSelectionServiceTests
{
    [Fact]
    public async Task ResolveAsync_selects_and_remembers_the_Windows_default_microphone()
    {
        var store = new InMemoryConfigurationStore(new MicrophoneConfiguration());
        var service = CreateService(store, "mic-usb");

        var selected = await service.ResolveAsync();

        Assert.Equal("mic-usb", selected.Id);
        Assert.Equal("USB microphone", store.Current.DeviceName);
    }

    [Fact]
    public async Task ResolveAsync_skips_an_excluded_Windows_default_microphone()
    {
        var store = new InMemoryConfigurationStore(new MicrophoneConfiguration
        {
            DeviceId = "mic-headset",
            DeviceName = "Headset microphone",
            ExcludedDeviceIds = ["mic-usb"]
        });
        var service = CreateService(store, "mic-usb");

        var selected = await service.ResolveAsync();

        Assert.Equal("mic-headset", selected.Id);
    }

    [Fact]
    public async Task ResolveAsync_honors_manual_selection_when_automation_is_disabled()
    {
        var store = new InMemoryConfigurationStore(new MicrophoneConfiguration
        {
            DeviceId = "mic-headset",
            DeviceName = "Headset microphone",
            AutomaticallySelectDevice = false
        });
        var service = CreateService(store, "mic-usb");

        var selected = await service.ResolveAsync();

        Assert.Equal("mic-headset", selected.Id);
    }

    private static MicrophoneSelectionService CreateService(
        IMicrophoneConfigurationStore store,
        string defaultDeviceId) =>
        new(
            new SelectionMicrophoneController(),
            new FixedDefaultMicrophoneProvider(defaultDeviceId),
            store,
            NullLogger<MicrophoneSelectionService>.Instance);

    private sealed class FixedDefaultMicrophoneProvider(string deviceId) : IDefaultMicrophoneProvider
    {
        public string? GetDefaultDeviceId() => deviceId;
    }

    private sealed class InMemoryConfigurationStore(MicrophoneConfiguration configuration)
        : IMicrophoneConfigurationStore
    {
        public MicrophoneConfiguration Current { get; private set; } = configuration;

        public void Save(MicrophoneConfiguration updatedConfiguration) => Current = updatedConfiguration;
    }

    private sealed class SelectionMicrophoneController : IObsMicrophoneController
    {
        public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MicrophoneDevice>>(
            [
                new("default", "Default"),
                new("mic-usb", "USB microphone"),
                new("mic-headset", "Headset microphone")
            ]);

        public Task ConfigureAsync(
            MicrophoneDevice device,
            MicrophoneProcessingSettings processingSettings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(
            string deviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
