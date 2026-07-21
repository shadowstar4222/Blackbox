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
    ILogger<MicrophoneDeviceMonitor> logger,
    MicrophoneSelectionService? microphoneSelectionService = null) : IMicrophoneDeviceMonitor, IAsyncDisposable
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
        CancellationTokenSource? monitorCancellation;
        lock (_sync)
        {
            monitorTask = _monitorTask;
            monitorCancellation = _monitorCancellation;
        }

        if (monitorCancellation is not null)
        {
            await monitorCancellation.CancelAsync();
        }

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (monitorCancellation?.IsCancellationRequested == true)
            {
            }
        }

        lock (_sync)
        {
            if (ReferenceEquals(_monitorTask, monitorTask))
            {
                _monitorCancellation?.Dispose();
                _monitorCancellation = null;
                _monitorTask = null;
            }
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
                var selectionChanged = false;
                if (configuration.AutomaticallySelectDevice && microphoneSelectionService is not null)
                {
                    var selected = await microphoneSelectionService.ResolveAsync(cancellationToken);
                    selectionChanged = !configuration.DeviceId.Equals(
                        selected.Id,
                        StringComparison.OrdinalIgnoreCase);
                    configuration = configurationStore.Current;
                }

                var current = await microphoneController.GetDeviceStatusAsync(
                    configuration.DeviceId,
                    cancellationToken);
                Volatile.Write(ref _currentStatus, current);
                if (current.State == MicrophoneConnectionState.Disconnected &&
                    previous.State != MicrophoneConnectionState.Disconnected)
                {
                    logger.LogWarning(
                        "The configured microphone disconnected; OBS sources remain active to preserve silence and track timing.");
                }
                else if (current.State == MicrophoneConnectionState.Connected &&
                    (selectionChanged || previous.State == MicrophoneConnectionState.Disconnected))
                {
                    await microphoneController.ConfigureAsync(
                        new MicrophoneDevice(configuration.DeviceId, configuration.DeviceName),
                        configuration.ProcessingSettings,
                        cancellationToken);
                    logger.LogInformation(
                        "Restored microphone routing to both paths. Reason={RoutingReason}.",
                        selectionChanged ? "Windows default changed" : "device reconnected");
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
