using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class ObsAutoSetupService(
    IObsController obsController,
    ObsSetupPlanner planner,
    ILogger<ObsAutoSetupService> logger)
{
    public async Task<ObsConnectionStatus> SetupAsync(
        ObsConnectionSettings connectionSettings,
        RecordingSettings recordingSettings,
        CancellationToken cancellationToken = default)
    {
        connectionSettings.Validate();
        recordingSettings.Validate();

        var connectionStatus = await obsController.TestConnectionAsync(connectionSettings, cancellationToken);
        if (!connectionStatus.IsConnected)
        {
            return connectionStatus;
        }

        var plan = planner.CreateDefaultPlan(recordingSettings);
        plan.Validate();
        await obsController.ApplySetupPlanAsync(connectionSettings, plan, cancellationToken);
        logger.LogInformation("Applied automatic OBS setup plan {ProfileName}.", plan.ProfileName);
        return ObsConnectionStatus.Connected("OBS setup applied.");
    }
}
