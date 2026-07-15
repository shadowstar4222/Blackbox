namespace Blackbox.Infrastructure;

using Blackbox.Domain;

public interface IObsController
{
    Task LaunchAsync(CancellationToken cancellationToken = default);
    Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes, CancellationToken cancellationToken = default);
    Task ConfigureAudioAsync(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings, CancellationToken cancellationToken = default);
    Task StartRecordingAsync(CancellationToken cancellationToken = default);
    Task StopRecordingAsync(CancellationToken cancellationToken = default);
}
