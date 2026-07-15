using Microsoft.Extensions.Logging;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class ObsWebSocketController(
    IObsWebSocketRpcClient rpcClient,
    ObsSetupRequestBuilder requestBuilder,
    ILogger<ObsWebSocketController> logger) : IObsController
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
        return rpcClient.TestConnectionAsync(settings, cancellationToken);
    }

    public Task ApplySetupPlanAsync(ObsConnectionSettings settings, ObsSetupPlan plan, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        plan.Validate();
        var requests = requestBuilder.BuildSetupRequests(plan);
        logger.LogInformation(
            "OBS automatic setup requested. Profile={ProfileName}, SceneCollection={SceneCollectionName}, Scene={SceneName}, Sources={SourceCount}, Filters={FilterCount}",
            plan.ProfileName,
            plan.SceneCollectionName,
            plan.SceneName,
            plan.Sources.Count,
            plan.Filters.Count);
        return rpcClient.SendBatchAsync(settings, requests, cancellationToken);
    }

    public Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "OBS segmented recording configuration requested. Directory={RecordingDirectory}, SegmentMinutes={SegmentMinutes}",
            recordingDirectory,
            segmentMinutes);
        var settings = new ObsConnectionSettings();
        var requests = requestBuilder.BuildRecordingConfigurationRequests(recordingDirectory);
        return rpcClient.SendBatchAsync(settings, requests, cancellationToken);
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
        var settings = new ObsConnectionSettings();
        var requests = requestBuilder.BuildAudioRequests(profile, microphoneSettings);
        return rpcClient.SendBatchAsync(settings, requests, cancellationToken);
    }

    public Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("OBS recording start requested.");
        return rpcClient.SendRequestAsync(new ObsConnectionSettings(), new ObsRequest("StartRecord"), cancellationToken);
    }

    public Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("OBS recording stop requested.");
        return rpcClient.SendRequestAsync(new ObsConnectionSettings(), new ObsRequest("StopRecord"), cancellationToken);
    }
}
