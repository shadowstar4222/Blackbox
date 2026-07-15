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
    public async Task StartAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
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

        logger.LogInformation("Blackbox recording started with {SegmentDurationMinutes}-minute segments.", settings.SegmentDurationMinutes);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Blackbox recording stop requested.");
        try
        {
            await microphoneDeviceMonitor.StopAsync(cancellationToken);
        }
        finally
        {
            await obsController.StopRecordingAsync(cancellationToken);
        }
    }
}
