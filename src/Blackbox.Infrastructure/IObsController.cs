namespace Blackbox.Infrastructure;

public interface IObsController
{
    Task LaunchAsync(CancellationToken cancellationToken = default);
    Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes, CancellationToken cancellationToken = default);
    Task StartRecordingAsync(CancellationToken cancellationToken = default);
    Task StopRecordingAsync(CancellationToken cancellationToken = default);
}
