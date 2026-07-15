using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class RecordingCoordinator(
    IObsController obsController,
    ISegmentRepository segmentRepository,
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
        await obsController.StartRecordingAsync(cancellationToken);
        logger.LogInformation("Blackbox recording started with {SegmentDurationMinutes}-minute segments.", settings.SegmentDurationMinutes);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Blackbox recording stop requested.");
        return obsController.StopRecordingAsync(cancellationToken);
    }
}
