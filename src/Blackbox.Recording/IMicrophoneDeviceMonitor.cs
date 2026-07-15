using Blackbox.Domain;

namespace Blackbox.Recording;

public interface IMicrophoneDeviceMonitor
{
    MicrophoneDeviceStatus CurrentStatus { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
