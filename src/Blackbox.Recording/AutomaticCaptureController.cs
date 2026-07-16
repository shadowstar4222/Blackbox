using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class AutomaticCaptureController(
    IObsController obsController,
    RecordingCoordinator recordingCoordinator,
    RecordingSettings recordingSettings,
    IClock clock,
    AutomaticCaptureOptions options,
    ILogger<AutomaticCaptureController> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GameCaptureTarget? _pendingTarget;
    private GameCaptureTarget? _activeTarget;
    private DateTimeOffset? _lastDetectedAt;
    private int _positiveDetections;
    private bool _ownsRecording;
    private bool _isEnabled;
    private AutomaticCaptureStatus _status = new(
        AutomaticCaptureState.Disabled,
        "Automatic capture is off.",
        null,
        false);

    public event Action<AutomaticCaptureStatus>? StatusChanged;

    public bool IsEnabled => Volatile.Read(ref _isEnabled);
    public AutomaticCaptureStatus Status => Volatile.Read(ref _status);

    public void Enable()
    {
        options.Validate();
        if (IsEnabled)
        {
            return;
        }

        Volatile.Write(ref _isEnabled, true);
        Publish(new AutomaticCaptureStatus(
            AutomaticCaptureState.Watching,
            "Watching for a foreground Steam game.",
            null,
            recordingCoordinator.IsRecording));
    }

    public async Task ProcessDetectionAsync(
        GameCaptureTarget? target,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsEnabled)
            {
                return;
            }

            if (_ownsRecording && !recordingCoordinator.IsRecording)
            {
                ResetDetection();
                _ownsRecording = false;
            }

            if (target is null)
            {
                await ProcessMissingGameAsync(cancellationToken);
                return;
            }

            target.Validate();
            _lastDetectedAt = clock.UtcNow;
            if (_activeTarget?.Identity == target.Identity)
            {
                Publish(new AutomaticCaptureStatus(
                    AutomaticCaptureState.Recording,
                    recordingCoordinator.IsRecording
                        ? $"Recording {target.Title}."
                        : $"Tracking {target.Title}; recording is currently stopped.",
                    target,
                    recordingCoordinator.IsRecording));
                return;
            }

            if (_pendingTarget?.Identity == target.Identity)
            {
                _positiveDetections++;
            }
            else
            {
                _pendingTarget = target;
                _positiveDetections = 1;
            }

            if (_positiveDetections < options.RequiredPositiveDetections)
            {
                Publish(new AutomaticCaptureStatus(
                    AutomaticCaptureState.Confirming,
                    $"Detected {target.Title}; confirming the game is stable.",
                    target,
                    recordingCoordinator.IsRecording));
                return;
            }

            Publish(new AutomaticCaptureStatus(
                AutomaticCaptureState.Starting,
                $"Binding OBS to {target.Title}...",
                target,
                recordingCoordinator.IsRecording));
            await obsController.ConfigureGameCaptureAsync(target, cancellationToken);
            if (!recordingCoordinator.IsRecording)
            {
                _ownsRecording = await recordingCoordinator.TryStartAsync(recordingSettings, cancellationToken);
            }

            _activeTarget = target;
            _pendingTarget = null;
            _positiveDetections = 0;
            logger.LogInformation(
                "Automatic capture activated for {GameTitle}. OwnsRecording={OwnsRecording}.",
                target.Title,
                _ownsRecording);
            Publish(new AutomaticCaptureStatus(
                AutomaticCaptureState.Recording,
                _ownsRecording ? $"Recording {target.Title}." : $"Tracking {target.Title} during manual recording.",
                target,
                recordingCoordinator.IsRecording));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref _isEnabled, false);
            if (_ownsRecording && recordingCoordinator.IsRecording)
            {
                Publish(new AutomaticCaptureStatus(
                    AutomaticCaptureState.Stopping,
                    "Stopping the automatic recording...",
                    _activeTarget,
                    true));
                await recordingCoordinator.TryStopAsync(cancellationToken);
            }

            _ownsRecording = false;
            ResetDetection();
            Publish(new AutomaticCaptureStatus(
                AutomaticCaptureState.Disabled,
                "Automatic capture is off.",
                null,
                recordingCoordinator.IsRecording));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ReportFailure(Exception exception)
    {
        logger.LogWarning(exception, "Automatic capture check failed.");
        Publish(new AutomaticCaptureStatus(
            AutomaticCaptureState.Faulted,
            $"Automatic capture failed: {exception.Message}",
            _activeTarget ?? _pendingTarget,
            recordingCoordinator.IsRecording));
    }

    private async Task ProcessMissingGameAsync(CancellationToken cancellationToken)
    {
        _pendingTarget = null;
        _positiveDetections = 0;
        if (_activeTarget is null || _lastDetectedAt is null)
        {
            Publish(new AutomaticCaptureStatus(
                AutomaticCaptureState.Watching,
                "Watching for a foreground Steam game.",
                null,
                recordingCoordinator.IsRecording));
            return;
        }

        var remaining = options.StopGracePeriod - (clock.UtcNow - _lastDetectedAt.Value);
        if (remaining > TimeSpan.Zero)
        {
            Publish(new AutomaticCaptureStatus(
                AutomaticCaptureState.Recording,
                $"{_activeTarget.Title} is unavailable; stopping in {Math.Ceiling(remaining.TotalSeconds):0} seconds.",
                _activeTarget,
                recordingCoordinator.IsRecording));
            return;
        }

        if (_ownsRecording && recordingCoordinator.IsRecording)
        {
            Publish(new AutomaticCaptureStatus(
                AutomaticCaptureState.Stopping,
                $"Stopping after {_activeTarget.Title} closed.",
                _activeTarget,
                true));
            await recordingCoordinator.TryStopAsync(cancellationToken);
        }

        logger.LogInformation("Automatic capture released {GameTitle} after the stop grace period.", _activeTarget.Title);
        _ownsRecording = false;
        ResetDetection();
        Publish(new AutomaticCaptureStatus(
            AutomaticCaptureState.Watching,
            "Watching for a foreground Steam game.",
            null,
            recordingCoordinator.IsRecording));
    }

    private void ResetDetection()
    {
        _pendingTarget = null;
        _activeTarget = null;
        _lastDetectedAt = null;
        _positiveDetections = 0;
    }

    private void Publish(AutomaticCaptureStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(status);
    }
}
