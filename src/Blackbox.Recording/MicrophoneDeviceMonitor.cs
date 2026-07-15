using System.Net.WebSockets;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class MicrophoneDeviceMonitor(
    IObsMicrophoneController microphoneController,
    IMicrophoneConfigurationStore configurationStore,
    IClock clock,
    MicrophoneMonitoringOptions options,
    ILogger<MicrophoneDeviceMonitor> logger) : IMicrophoneDeviceMonitor
{
    private readonly object _sync = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private MicrophoneDeviceStatus _currentStatus = new(
        configurationStore.Current.DeviceId,
        MicrophoneConnectionState.Unknown,
        clock.UtcNow);

    public MicrophoneDeviceStatus CurrentStatus => Volatile.Read(ref _currentStatus);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_monitorTask is not null)
            {
                return Task.CompletedTask;
            }

            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = MonitorAsync(_monitorCancellation.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? monitorTask;
        lock (_sync)
        {
            monitorTask = _monitorTask;
            _monitorCancellation?.Cancel();
        }

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (_monitorCancellation?.IsCancellationRequested == true)
            {
            }
        }

        lock (_sync)
        {
            _monitorCancellation?.Dispose();
            _monitorCancellation = null;
            _monitorTask = null;
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var configuration = configurationStore.Current;
            try
            {
                var previous = CurrentStatus;
                var current = await microphoneController.GetDeviceStatusAsync(
                    configuration.DeviceId,
                    cancellationToken);
                Volatile.Write(ref _currentStatus, current);
                if (current.State == MicrophoneConnectionState.Disconnected &&
                    previous.State != MicrophoneConnectionState.Disconnected)
                {
                    logger.LogWarning(
                        "Microphone {MicrophoneDeviceId} disconnected; OBS sources remain active to preserve silence and track timing.",
                        configuration.DeviceId);
                }
                else if (current.State == MicrophoneConnectionState.Connected &&
                    previous.State == MicrophoneConnectionState.Disconnected)
                {
                    await microphoneController.ConfigureAsync(
                        new MicrophoneDevice(configuration.DeviceId, configuration.DeviceName),
                        configuration.ProcessingSettings,
                        cancellationToken);
                    logger.LogInformation(
                        "Microphone {MicrophoneDeviceId} reconnected and was restored to both microphone paths.",
                        configuration.DeviceId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or WebSocketException)
            {
                Volatile.Write(ref _currentStatus, new MicrophoneDeviceStatus(
                    configuration.DeviceId,
                    MicrophoneConnectionState.Unknown,
                    clock.UtcNow));
                logger.LogDebug(ex, "Could not refresh microphone connection state.");
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
