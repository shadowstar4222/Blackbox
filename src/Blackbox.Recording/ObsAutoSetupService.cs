using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class ObsAutoSetupService(
    IObsPortableProvisioner provisioner,
    IObsConnectionSettingsProvider connectionSettingsProvider,
    IObsController obsController,
    ObsSetupPlanner planner,
    ObsOnboardingOptions options,
    ILogger<ObsAutoSetupService> logger)
{
    public async Task<ObsSetupResult> SetupAsync(
        RecordingSettings recordingSettings,
        IProgress<ObsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordingSettings);
        recordingSettings.Validate();
        options.Validate();

        try
        {
            var connectionSettings = connectionSettingsProvider.Current;
            var connectionStatus = connectionSettings.UseAuthentication
                ? await obsController.TestConnectionAsync(connectionSettings, cancellationToken)
                : ObsConnectionStatus.Failed("No saved Blackbox OBS connection is available.");
            if (!connectionStatus.IsConnected)
            {
                var installation = await provisioner.EnsureInstalledAsync(progress, cancellationToken);
                connectionSettings = CreateConnectionSettings();
                connectionSettingsProvider.Set(connectionSettings);

                progress?.Report(new ObsSetupProgress(ObsSetupStage.Launching, "Starting the private OBS backend..."));
                await provisioner.LaunchAsync(installation, connectionSettings, cancellationToken);

                progress?.Report(new ObsSetupProgress(ObsSetupStage.Connecting, "Waiting for OBS to become ready..."));
                connectionStatus = await WaitForConnectionAsync(connectionSettings, cancellationToken);
            }

            if (!connectionStatus.IsConnected)
            {
                return ObsSetupResult.Failed(
                    $"OBS started but Blackbox could not connect to it. {connectionStatus.Message}");
            }

            progress?.Report(new ObsSetupProgress(ObsSetupStage.Configuring, "Creating the Blackbox recording profile..."));
            var plan = planner.CreateDefaultPlan(recordingSettings);
            await ApplySetupWhenReadyAsync(
                connectionSettings,
                plan,
                progress,
                cancellationToken);

            progress?.Report(new ObsSetupProgress(ObsSetupStage.TestingRecording, "Running a short recording check..."));
            var probePath = await RunRecordingProbeAsync(cancellationToken);
            progress?.Report(new ObsSetupProgress(ObsSetupStage.Complete, "OBS is ready for Blackbox.", 100));
            logger.LogInformation(
                "Completed one-click OBS setup with probe recording {ProbeRecordingPath}.",
                probePath);
            return ObsSetupResult.Successful("OBS is installed, configured, and ready.", probePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "One-click OBS setup failed.");
            return ObsSetupResult.Failed(ex.Message);
        }
    }

    private async Task<ObsConnectionStatus> WaitForConnectionAsync(
        ObsConnectionSettings connectionSettings,
        CancellationToken cancellationToken)
    {
        ObsConnectionStatus? lastStatus = null;
        for (var attempt = 0; attempt < options.ConnectionAttempts; attempt++)
        {
            lastStatus = await obsController.TestConnectionAsync(connectionSettings, cancellationToken);
            if (lastStatus.IsConnected)
            {
                return lastStatus;
            }

            if (options.ConnectionRetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.ConnectionRetryDelay, cancellationToken);
            }
        }

        return lastStatus ?? ObsConnectionStatus.Failed("No connection attempt was made.");
    }

    private async Task ApplySetupWhenReadyAsync(
        ObsConnectionSettings connectionSettings,
        ObsSetupPlan plan,
        IProgress<ObsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < options.ConnectionAttempts; attempt++)
        {
            try
            {
                await obsController.ApplySetupPlanAsync(
                    connectionSettings,
                    plan,
                    cancellationToken);
                return;
            }
            catch (ObsRequestFailedException ex) when (
                ex.FailedResponses.Count > 0 &&
                ex.FailedResponses.All(static response => response.Code == 207) &&
                attempt < options.ConnectionAttempts - 1)
            {
                progress?.Report(new ObsSetupProgress(
                    ObsSetupStage.Connecting,
                    "OBS is connected and still initializing..."));
                if (options.ConnectionRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(options.ConnectionRetryDelay, cancellationToken);
                }
            }
        }

        throw new InvalidOperationException("OBS did not become ready for configuration.");
    }

    private async Task<string> RunRecordingProbeAsync(CancellationToken cancellationToken)
    {
        var started = false;
        try
        {
            await obsController.StartRecordingAsync(cancellationToken);
            started = true;
            if (options.ProbeRecordingDuration > TimeSpan.Zero)
            {
                await Task.Delay(options.ProbeRecordingDuration, cancellationToken);
            }

            var outputPath = await obsController.StopRecordingAsync(cancellationToken);
            started = false;
            return await RecordingOutputFileWaiter.WaitAsync(
                outputPath,
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        finally
        {
            if (started)
            {
                try
                {
                    await obsController.StopRecordingAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Could not stop OBS after a failed recording probe.");
                }
            }
        }
    }

    private static ObsConnectionSettings CreateConnectionSettings()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        int port;
        try
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
        return new ObsConnectionSettings
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24))
        };
    }
}
