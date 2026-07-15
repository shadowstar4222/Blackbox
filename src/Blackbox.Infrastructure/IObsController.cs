namespace Blackbox.Infrastructure;

using Blackbox.Domain;

public interface IObsController
{
    Task LaunchAsync(CancellationToken cancellationToken = default);
    Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default);
    Task ApplySetupPlanAsync(ObsConnectionSettings settings, ObsSetupPlan plan, CancellationToken cancellationToken = default);
    Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes, CancellationToken cancellationToken = default);
    Task ConfigureAudioAsync(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings, CancellationToken cancellationToken = default);
    Task StartRecordingAsync(CancellationToken cancellationToken = default);
    Task<string?> StopRecordingAsync(CancellationToken cancellationToken = default);
}
