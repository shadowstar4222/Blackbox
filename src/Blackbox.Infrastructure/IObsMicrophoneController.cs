using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public interface IObsMicrophoneController
{
    Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(string deviceId, CancellationToken cancellationToken = default);
    Task ConfigureAsync(
        MicrophoneDevice device,
        MicrophoneProcessingSettings processingSettings,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(
        TimeSpan duration,
        IProgress<AudioLevelSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
    Task SetProcessingEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default);
}
