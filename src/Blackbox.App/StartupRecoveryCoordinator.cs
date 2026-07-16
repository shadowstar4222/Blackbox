using Blackbox.Domain;
using Blackbox.Export;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public sealed class StartupRecoveryCoordinator(
    RecordingRecoveryService recordingRecovery,
    RecordingLibraryService recordingLibrary,
    IObsController obsController,
    IObsConnectionSettingsProvider connectionSettingsProvider,
    ObsAutoSetupService obsAutoSetup,
    RecordingCoordinator recordingCoordinator,
    AutomaticCaptureService automaticCapture,
    RecordingSettings recordingSettings,
    StartupRecoveryState state,
    ILogger<StartupRecoveryCoordinator> logger)
{
    public async Task<StartupRecoveryOutcome> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Checking recordings after the previous shutdown...");
        var recoveryResult = await recordingRecovery.RecoverAsync(
            new Progress<RecordingRecoveryProgress>(update => progress?.Report(update.Message)),
            cancellationToken);

        progress?.Report("Refreshing the recording index...");
        var sessions = await recordingLibrary.RefreshAsync(
            new Progress<RecordingLibraryProgress>(update => progress?.Report(update.Message)),
            cancellationToken);

        var obsReady = false;
        var recordingAdopted = false;
        var connectionSettings = connectionSettingsProvider.Current;
        if (connectionSettings.UseAuthentication)
        {
            try
            {
                var connection = await obsController.TestConnectionAsync(connectionSettings, cancellationToken);
                obsReady = connection.IsConnected;
                if (obsReady)
                {
                    recordingAdopted = await recordingCoordinator.TryAdoptExistingRecordingAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not reconnect to the previous private OBS instance during startup recovery.");
            }
        }

        if (automaticCapture.WasInterrupted && !obsReady)
        {
            progress?.Report("Restarting OBS for interrupted automatic capture...");
            var setupResult = await obsAutoSetup.SetupAsync(
                recordingSettings,
                new Progress<ObsSetupProgress>(update => progress?.Report(update.Message)),
                cancellationToken);
            obsReady = setupResult.IsSuccessful;
        }

        var automaticCaptureResumed = obsReady && automaticCapture.WasInterrupted &&
            await automaticCapture.ResumeAfterCrashAsync(recordingAdopted, cancellationToken);
        var outcome = new StartupRecoveryOutcome(
            recoveryResult,
            sessions.Count,
            obsReady,
            recordingAdopted,
            automaticCaptureResumed);
        state.LastOutcome = outcome;
        logger.LogInformation(
            "Startup recovery outcome. ObsReady={ObsReady}, RecordingAdopted={RecordingAdopted}, AutomaticCaptureResumed={AutomaticCaptureResumed}, IndexedSessions={SessionCount}.",
            obsReady,
            recordingAdopted,
            automaticCaptureResumed,
            sessions.Count);
        return outcome;
    }
}
