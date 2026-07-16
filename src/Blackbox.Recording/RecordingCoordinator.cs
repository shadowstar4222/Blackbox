using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class RecordingCoordinator(
    IObsController obsController,
    IObsMicrophoneController microphoneController,
    IMicrophoneConfigurationStore microphoneConfigurationStore,
    ISegmentRepository segmentRepository,
    IMicrophoneDeviceMonitor microphoneDeviceMonitor,
    ILogger<RecordingCoordinator> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _isRecording;

    public bool IsRecording => Volatile.Read(ref _isRecording) == 1;

    public async Task StartAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        await TryStartAsync(settings, cancellationToken);
    }

    public async Task<bool> TryStartAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRecording)
            {
                return false;
            }

            settings.Validate();
            Directory.CreateDirectory(settings.RecordingLocation);
            await segmentRepository.InitializeAsync(cancellationToken);
            await obsController.LaunchAsync(cancellationToken);
            await obsController.ConfigureSegmentedRecordingAsync(
                settings.RecordingLocation,
                settings.SegmentDurationMinutes,
                cancellationToken);
            var microphoneConfiguration = microphoneConfigurationStore.Current;
            await obsController.ConfigureAudioAsync(
                AudioRoutingProfile.Default,
                microphoneConfiguration.ProcessingSettings,
                cancellationToken);
            await microphoneController.ConfigureAsync(
                new MicrophoneDevice(
                    microphoneConfiguration.DeviceId,
                    microphoneConfiguration.DeviceName),
                microphoneConfiguration.ProcessingSettings,
                cancellationToken);
            await obsController.StartRecordingAsync(cancellationToken);
            try
            {
                await microphoneDeviceMonitor.StartAsync(cancellationToken);
            }
            catch
            {
                await obsController.StopRecordingAsync(CancellationToken.None);
                throw;
            }

            Volatile.Write(ref _isRecording, 1);
            logger.LogInformation("Blackbox recording started with {SegmentDurationMinutes}-minute segments.", settings.SegmentDurationMinutes);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await TryStopAsync(cancellationToken);
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

            logger.LogInformation("Blackbox recording stop requested.");
            try
            {
                await microphoneDeviceMonitor.StopAsync(cancellationToken);
            }
            finally
            {
                await obsController.StopRecordingAsync(cancellationToken);
                Volatile.Write(ref _isRecording, 0);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
