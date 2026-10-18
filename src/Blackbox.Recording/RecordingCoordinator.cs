using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Storage;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class RecordingCoordinator(
    IObsController obsController,
    IObsMicrophoneController microphoneController,
    IMicrophoneConfigurationStore microphoneConfigurationStore,
    ISegmentRepository segmentRepository,
    IMicrophoneDeviceMonitor microphoneDeviceMonitor,
    ILogger<RecordingCoordinator> logger,
    MicrophoneSelectionService? microphoneSelectionService = null,
    GameProfileSettingsService? gameProfileSettingsService = null,
    RecordingQualityContext? recordingQualityContext = null,
    StorageQuotaEnforcer? storageQuotaEnforcer = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _isRecording;
    private int _isTransitioning;
    private bool _nativeEventsSubscribed;
    private bool _monitorActive;

    public event Action<bool>? RecordingStateChanged;

    public bool IsRecording => Volatile.Read(ref _isRecording) == 1;
    public bool IsTransitioning => Volatile.Read(ref _isTransitioning) == 1;

    public async Task StartAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        await TryStartAsync(settings, null, cancellationToken);
    }

    public async Task<bool> TryStartAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        return await TryStartAsync(settings, null, cancellationToken);
    }

    public async Task<bool> TryStartAsync(
        RecordingSettings settings,
        GameCaptureTarget? captureTarget,
        CancellationToken cancellationToken = default)
    {
        var effective = captureTarget is not null && gameProfileSettingsService is not null
            ? await gameProfileSettingsService.ResolveAsync(captureTarget, cancellationToken)
            : gameProfileSettingsService?.GlobalDefaults;
        return await TryStartAsync(settings, captureTarget, effective, cancellationToken);
    }

    public async Task<bool> TryStartAsync(
        RecordingSettings settings,
        GameCaptureTarget? captureTarget,
        EffectiveGameProfileSettings? effectiveProfile,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording)
            {
                return false;
            }

            Volatile.Write(ref _isTransitioning, 1);
            if (!_nativeEventsSubscribed && obsController is INativeRecorderController nativeRecorder)
            {
                nativeRecorder.RecordingFailed += NativeRecordingFailed;
                nativeRecorder.TargetClosed += NativeTargetClosed;
                _nativeEventsSubscribed = true;
            }
            var effectiveSettings = effectiveProfile is null
                ? settings
                : effectiveProfile.Recording with
                {
                    RecordingLocation = settings.RecordingLocation
                };
            effectiveSettings.Validate();
            if (effectiveProfile is not null)
            {
                recordingQualityContext?.Activate(effectiveProfile);
            }

            Directory.CreateDirectory(effectiveSettings.RecordingLocation);
            await segmentRepository.InitializeAsync(cancellationToken);
            if (storageQuotaEnforcer is not null)
            {
                await storageQuotaEnforcer.EnforceAsync(effectiveSettings, cancellationToken);
            }

            await obsController.LaunchAsync(cancellationToken);
            await obsController.ConfigureSegmentedRecordingAsync(
                effectiveSettings.RecordingLocation,
                effectiveSettings.SegmentDurationMinutes,
                captureTarget,
                cancellationToken);
            var microphoneConfiguration = microphoneConfigurationStore.Current;
            var microphoneProcessing =
                effectiveProfile?.MicrophoneProcessing ?? microphoneConfiguration.ProcessingSettings;
            var audioRouting = effectiveProfile?.CaptureVoiceChat == false
                ? AudioRoutingProfile.Default with
                {
                    ApplicationAssignments = [],
                    DisableDesktopAudioWhenIsolatedSourcesAreActive = false
                }
                : AudioRoutingProfile.Default;
            await obsController.ConfigureAudioAsync(
                audioRouting,
                microphoneProcessing,
                cancellationToken);
            var captureMicrophone = effectiveProfile?.CaptureMicrophone != false;
            if (obsController is INativeRecorderController && captureMicrophone)
            {
                var devices = await microphoneController.GetDevicesAsync(cancellationToken);
                if (devices.Count == 0 || microphoneConfiguration.AutomaticallySelectDevice &&
                    devices.All(device => microphoneConfiguration.ExcludedDeviceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase)))
                {
                    captureMicrophone = false;
                    logger.LogWarning("No allowed microphone is connected; the recording will contain application audio only.");
                }
            }
            if (captureMicrophone && microphoneSelectionService is not null)
            {
                await microphoneSelectionService.ApplyAsync(
                    microphoneProcessing,
                    cancellationToken);
            }
            else if (captureMicrophone)
            {
                await microphoneController.ConfigureAsync(
                    new MicrophoneDevice(
                        microphoneConfiguration.DeviceId,
                        microphoneConfiguration.DeviceName),
                    microphoneProcessing,
                    cancellationToken);
            }

            await microphoneController.SetMutedAsync(
                !captureMicrophone,
                cancellationToken);
            if (effectiveProfile is not null)
            {
                gameProfileSettingsService?.WriteSessionMetadata(
                    effectiveSettings.RecordingLocation,
                    captureTarget,
                    effectiveProfile);
            }

            await obsController.StartRecordingAsync(cancellationToken);
            try
            {
                if (captureMicrophone)
                {
                    await microphoneDeviceMonitor.StartAsync(cancellationToken);
                    _monitorActive = true;
                }
            }
            catch
            {
                await obsController.StopRecordingAsync(CancellationToken.None);
                throw;
            }

            Volatile.Write(ref _isRecording, 1);
            RecordingStateChanged?.Invoke(true);
            logger.LogInformation(
                "Blackbox recording started with {SegmentDurationMinutes}-minute segments and profile {ProfileName}.",
                effectiveSettings.SegmentDurationMinutes,
                effectiveProfile?.ProfileName ?? "Global defaults");
            return true;
        }
        catch
        {
            Volatile.Write(ref _isRecording, 0);
            recordingQualityContext?.Clear();
            throw;
        }
        finally
        {
            Volatile.Write(ref _isTransitioning, 0);
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await TryStopAsync(cancellationToken);
    }

    public async Task<bool> TryAdoptExistingRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording)
            {
                return true;
            }

            Volatile.Write(ref _isTransitioning, 1);
            var status = await obsController.GetRecordingStatusAsync(cancellationToken);
            if (!status.IsActive)
            {
                return false;
            }

            try
            {
                await microphoneDeviceMonitor.StartAsync(cancellationToken);
                _monitorActive = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Microphone monitoring could not resume for the surviving OBS recording.");
            }

            Volatile.Write(ref _isRecording, 1);
            RecordingStateChanged?.Invoke(true);
            logger.LogWarning(
                "Adopted a surviving OBS recording after restart. Duration={Duration}, BytesWritten={BytesWritten}.",
                status.Duration,
                status.BytesWritten);
            return true;
        }
        finally
        {
            Volatile.Write(ref _isTransitioning, 0);
            _gate.Release();
        }
    }

    public async Task<bool> TryStopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsRecording)
            {
                return false;
            }

            Volatile.Write(ref _isTransitioning, 1);
            logger.LogInformation("Blackbox recording stop requested.");
            try
            {
                if (_monitorActive)
                {
                    await microphoneDeviceMonitor.StopAsync(cancellationToken);
                    _monitorActive = false;
                }
            }
            finally
            {
                await obsController.StopRecordingAsync(cancellationToken);
                Volatile.Write(ref _isRecording, 0);
                recordingQualityContext?.Clear();
                RecordingStateChanged?.Invoke(false);
            }

            return true;
        }
        finally
        {
            Volatile.Write(ref _isTransitioning, 0);
            _gate.Release();
        }
    }

    private void NativeRecordingFailed(string message)
    {
        if (Interlocked.Exchange(ref _isRecording, 0) == 0) return;
        recordingQualityContext?.Clear();
        logger.LogError("Native recording ended unexpectedly: {Message}", message);
        RecordingStateChanged?.Invoke(false);
        _ = StopMonitorAfterFailureAsync();
    }

    private void NativeTargetClosed()
    {
        if (Interlocked.Exchange(ref _isRecording, 0) == 0) return;
        recordingQualityContext?.Clear();
        logger.LogInformation("Recording finalized because its window closed; automatic capture remains available.");
        RecordingStateChanged?.Invoke(false);
        _ = StopMonitorAfterFailureAsync();
    }

    private async Task StopMonitorAfterFailureAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // A replacement session may already have started after the failure notification.
            if (!IsRecording && _monitorActive)
            {
                await microphoneDeviceMonitor.StopAsync();
                _monitorActive = false;
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Microphone monitoring could not stop after capture failed."); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_nativeEventsSubscribed && obsController is INativeRecorderController native)
        {
            native.RecordingFailed -= NativeRecordingFailed;
            native.TargetClosed -= NativeTargetClosed;
        }
        _gate.Dispose();
    }
}
