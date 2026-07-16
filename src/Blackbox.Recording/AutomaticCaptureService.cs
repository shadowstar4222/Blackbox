using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class AutomaticCaptureService(
    IGameProcessDetector gameProcessDetector,
    AutomaticCaptureController controller,
    IAutomaticCapturePreferenceStore preferenceStore,
    AutomaticCaptureOptions options,
    ILogger<AutomaticCaptureService> logger)
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;

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
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (enabled == IsEnabled)
            {
                return;
            }

            if (enabled)
            {
                options.Validate();
                preferenceStore.Save(true);
                controller.Enable();
                _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _loopTask = RunAsync(_loopCancellation.Token);
                return;
            }

            _loopCancellation?.Cancel();
            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _loopCancellation?.Dispose();
            _loopCancellation = null;
            _loopTask = null;
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
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!preferenceStore.WasEnabled || IsEnabled)
            {
                return false;
            }

            options.Validate();
            controller.Enable();
            if (ownsExistingRecording)
            {
                controller.AdoptRecordingOwnership();
            }

            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
}
