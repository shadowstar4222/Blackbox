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
    RecordingCoordinator recordingCoordinator,
    AutomaticCaptureService automaticCapture,
    StartupRecoveryState state,
    ILogger<StartupRecoveryCoordinator> logger)
{
    public async Task<StartupRecoveryOutcome> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var recorderReady = false;
        string? recorderFailure = null;
        progress?.Report("Checking the local recording engine...");
        try
        {
            await obsController.LaunchAsync(cancellationToken);
            recorderReady = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            recorderFailure = ex.Message;
            logger.LogWarning(ex, "The local recording engine is unavailable.");
        }

        progress?.Report("Checking recordings after the previous shutdown...");
        var recoveryResult = await recordingRecovery.RecoverAsync(
            new Progress<RecordingRecoveryProgress>(update => progress?.Report(update.Message)),
            cancellationToken);

        progress?.Report("Refreshing the recording index...");
        var sessions = await recordingLibrary.RefreshAsync(
            new Progress<RecordingLibraryProgress>(update => progress?.Report(update.Message)),
            cancellationToken);

        var recordingAdopted = false;
        if (recorderReady)
        {
            try
            {
                recordingAdopted = await recordingCoordinator.TryAdoptExistingRecordingAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not recover the previous recording state.");
            }
        }

        var automaticCaptureResumed = recorderReady && automaticCapture.WasInterrupted &&
            await automaticCapture.ResumeAfterCrashAsync(recordingAdopted, cancellationToken);
        var outcome = new StartupRecoveryOutcome(
            recoveryResult,
            sessions.Count,
            recorderReady,
            recordingAdopted,
            automaticCaptureResumed)
        {
            RecorderFailure = recorderFailure
        };
        state.LastOutcome = outcome;
        logger.LogInformation(
            "Startup recovery outcome. RecorderReady={RecorderReady}, RecordingAdopted={RecordingAdopted}, AutomaticCaptureResumed={AutomaticCaptureResumed}, IndexedSessions={SessionCount}.",
            recorderReady,
            recordingAdopted,
            automaticCaptureResumed,
            sessions.Count);
        return outcome;
    }
}
