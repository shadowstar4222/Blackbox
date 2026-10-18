using Blackbox.Export;
using Blackbox.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blackbox.App;

public sealed class InstantClipBackgroundService(
    InstantClipService instantClipService,
    MarkerRetentionService markerRetentionService,
    RecordingCoordinator recordingCoordinator,
    InstantClipOptions options,
    ILogger<InstantClipBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        using var timer = new PeriodicTimer(options.PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await instantClipService.ProcessPendingAsync(stoppingToken);
                await markerRetentionService.ReconcileAsync(
                    recordingCoordinator.IsRecording,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The instant clip background queue encountered an error.");
            }
        }
    }
}
