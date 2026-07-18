using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class AutomaticCaptureService(
    IGameProcessDetector gameProcessDetector,
    AutomaticCaptureController controller,
    IAutomaticCapturePreferenceStore preferenceStore,
    AutomaticCaptureOptions options,
    ILogger<AutomaticCaptureService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private int _isDisposed;

    public event Action<AutomaticCaptureStatus>? StatusChanged
    {
        add => controller.StatusChanged += value;
        remove => controller.StatusChanged -= value;
    }

    public bool IsEnabled => controller.IsEnabled;
    public bool WasInterrupted => preferenceStore.WasEnabled && !IsEnabled;
    public AutomaticCaptureStatus Status => controller.Status;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (enabled == IsEnabled)
            {
                return;
            }

            if (enabled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                options.Validate();
                preferenceStore.Save(true);
                controller.Enable();
                _loopCancellation = new CancellationTokenSource();
                _loopTask = RunAsync(_loopCancellation.Token);
                return;
            }

            await StopLoopAsync();
            await controller.DisableAsync(cancellationToken);
            preferenceStore.Save(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> ResumeAfterCrashAsync(
        bool ownsExistingRecording,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!preferenceStore.WasEnabled || IsEnabled)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            options.Validate();
            controller.Enable();
            if (ownsExistingRecording)
            {
                controller.AdoptRecordingOwnership();
            }

            _loopCancellation = new CancellationTokenSource();
            _loopTask = RunAsync(_loopCancellation.Token);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var target = await gameProcessDetector.DetectAsync(cancellationToken);
                await controller.ProcessDetectionAsync(target, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Automatic game detection iteration failed.");
                controller.ReportFailure(ex);
            }

            await Task.Delay(options.PollInterval, cancellationToken);
        }
    }

    private async Task StopLoopAsync()
    {
        var cancellation = _loopCancellation;
        var task = _loopTask;
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        cancellation.Dispose();
        _loopCancellation = null;
        _loopTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            await StopLoopAsync();
            if (controller.IsEnabled)
            {
                await controller.DisableAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic capture cleanup failed during shutdown.");
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
