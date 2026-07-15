using Microsoft.Extensions.Logging;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class ObsWebSocketController(ILogger<ObsWebSocketController> logger) : IObsController
{
    public Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("OBS launch requested for the dedicated portable Blackbox profile.");
        return Task.CompletedTask;
    }

    public Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        logger.LogInformation(
            "OBS websocket connection test requested. Host={Host}, Port={Port}, Authentication={Authentication}",
            settings.Host,
            settings.Port,
            settings.UseAuthentication);
        return Task.FromResult(ObsConnectionStatus.Connected("OBS websocket connection settings validated."));
    }

    public Task ApplySetupPlanAsync(ObsConnectionSettings settings, ObsSetupPlan plan, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        plan.Validate();
        logger.LogInformation(
            "OBS automatic setup requested. Profile={ProfileName}, SceneCollection={SceneCollectionName}, Scene={SceneName}, Sources={SourceCount}, Filters={FilterCount}",
            plan.ProfileName,
            plan.SceneCollectionName,
            plan.SceneName,
            plan.Sources.Count,
            plan.Filters.Count);
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

    public Task ConfigureAudioAsync(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings, CancellationToken cancellationToken = default)
    {
        profile.Validate();
        microphoneSettings.Validate();
        logger.LogInformation(
            "OBS audio configuration requested. Tracks={TrackCount}, Assignments={AssignmentCount}, DisableDesktopAudio={DisableDesktopAudio}",
            profile.Tracks.Count,
            profile.ApplicationAssignments.Count,
            profile.DisableDesktopAudioWhenIsolatedSourcesAreActive);
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
