using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class ObsWebSocketController(ILogger<ObsWebSocketController> logger) : IObsController
{
    public Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("OBS launch requested for the dedicated portable Blackbox profile.");
        return Task.CompletedTask;
    }

    public Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "OBS segmented recording configuration requested. Directory={RecordingDirectory}, SegmentMinutes={SegmentMinutes}",
            recordingDirectory,
            segmentMinutes);
        return Task.CompletedTask;
    }

    public Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("OBS recording start requested.");
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("OBS recording stop requested.");
        return Task.CompletedTask;
    }
}
