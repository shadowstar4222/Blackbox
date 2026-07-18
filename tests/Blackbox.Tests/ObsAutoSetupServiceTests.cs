using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsAutoSetupServiceTests
{
    [Fact]
    public async Task SetupAsync_provisions_launches_configures_and_records_probe()
    {
        var probePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(probePath, "probe");
            var provisioner = new RecordingProvisioner();
            var provider = new ObsConnectionSettingsProvider();
            var obs = new SetupObsController(ObsConnectionStatus.Connected(), probePath);
            var service = CreateService(provisioner, provider, obs);

            var result = await service.SetupAsync(new RecordingSettings { RecordingLocation = Path.GetTempPath() });

            Assert.True(result.IsSuccessful);
            Assert.Equal(probePath, result.ProbeRecordingPath);
            Assert.Equal(["EnsureInstalled", "Launch"], provisioner.Calls);
            Assert.Equal(["TestConnection", "ApplySetup:5", "Start", "Stop"], obs.Calls);
            Assert.True(provider.Current.UseAuthentication);
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    [Fact]
    public async Task SetupAsync_stops_before_configuration_when_connection_fails()
    {
        var provisioner = new RecordingProvisioner();
        var provider = new ObsConnectionSettingsProvider();
        var obs = new SetupObsController(ObsConnectionStatus.Failed("OBS is not reachable."), null);
        var service = CreateService(provisioner, provider, obs);

        var result = await service.SetupAsync(new RecordingSettings { RecordingLocation = Path.GetTempPath() });

        Assert.False(result.IsSuccessful);
        Assert.Equal(["TestConnection"], obs.Calls);
    }

    [Fact]
    public async Task SetupAsync_reuses_a_running_blackbox_obs_instance()
    {
        var probePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(probePath, "probe");
            var provisioner = new RecordingProvisioner();
            var provider = new ObsConnectionSettingsProvider();
            provider.Set(new ObsConnectionSettings { Port = 4567, Password = "saved-password" });
            var obs = new SetupObsController(ObsConnectionStatus.Connected(), probePath);
            var service = CreateService(provisioner, provider, obs);

            var result = await service.SetupAsync(new RecordingSettings { RecordingLocation = Path.GetTempPath() });

            Assert.True(result.IsSuccessful);
            Assert.Empty(provisioner.Calls);
            Assert.Equal(["TestConnection", "ApplySetup:5", "Start", "Stop"], obs.Calls);
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    [Fact]
    public async Task SetupAsync_waits_for_obs_to_finalize_the_probe_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var probePath = Path.Combine(root, "probe.mkv");
        Directory.CreateDirectory(root);
        try
        {
            var provisioner = new RecordingProvisioner();
            var provider = new ObsConnectionSettingsProvider();
            provider.Set(new ObsConnectionSettings { Port = 4567, Password = "saved-password" });
            var obs = new SetupObsController(ObsConnectionStatus.Connected(), probePath);
            var service = CreateService(provisioner, provider, obs);

            var setupTask = service.SetupAsync(new RecordingSettings { RecordingLocation = root });
            await Task.Delay(150);
            await File.WriteAllTextAsync(probePath, "finalized");
            var result = await setupTask;

            Assert.True(result.IsSuccessful);
            Assert.Equal(probePath, result.ProbeRecordingPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SetupAsync_retries_configuration_while_obs_reports_not_ready()
    {
        var probePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(probePath, "probe");
            var provisioner = new RecordingProvisioner();
            var provider = new ObsConnectionSettingsProvider();
            provider.Set(new ObsConnectionSettings { Port = 4567, Password = "saved-password" });
            var obs = new SetupObsController(
                ObsConnectionStatus.Connected(),
                probePath,
                readinessFailures: 2);
            var service = CreateService(
                provisioner,
                provider,
                obs,
                connectionAttempts: 3);

            var result = await service.SetupAsync(
                new RecordingSettings { RecordingLocation = Path.GetTempPath() });

            Assert.True(result.IsSuccessful);
            Assert.Equal(3, obs.Calls.Count(call => call == "ApplySetup:5"));
            Assert.Equal(["Start", "Stop"], obs.Calls.TakeLast(2).ToArray());
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private static ObsAutoSetupService CreateService(
        IObsPortableProvisioner provisioner,
        IObsConnectionSettingsProvider provider,
        IObsController controller,
        int connectionAttempts = 1) =>
        new(
            provisioner,
            provider,
            controller,
            new ObsSetupPlanner(),
            new ObsOnboardingOptions
            {
                ConnectionAttempts = connectionAttempts,
                ConnectionRetryDelay = TimeSpan.Zero,
                ProbeRecordingDuration = TimeSpan.Zero
            },
            NullLogger<ObsAutoSetupService>.Instance);

    private sealed class RecordingProvisioner : IObsPortableProvisioner
    {
        public List<string> Calls { get; } = [];

        public Task<ObsInstallation> EnsureInstalledAsync(
            IProgress<ObsSetupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("EnsureInstalled");
            return Task.FromResult(new ObsInstallation("C:\\OBS", "C:\\OBS\\bin\\64bit\\obs64.exe", "test"));
        }

        public Task LaunchAsync(
            ObsInstallation installation,
            ObsConnectionSettings connectionSettings,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("Launch");
            return Task.CompletedTask;
        }
    }

    private sealed class SetupObsController(
        ObsConnectionStatus status,
        string? probePath,
        int readinessFailures = 0) : IObsController
    {
        private int _remainingReadinessFailures = readinessFailures;
        public List<string> Calls { get; } = [];

        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ObsConnectionStatus> TestConnectionAsync(
            ObsConnectionSettings settings,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("TestConnection");
            return Task.FromResult(status);
        }

        public Task ApplySetupPlanAsync(
            ObsConnectionSettings settings,
            ObsSetupPlan plan,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"ApplySetup:{plan.Sources.Count}");
            if (Interlocked.Decrement(ref _remainingReadinessFailures) >= 0)
            {
                throw new ObsRequestFailedException(
                [new ObsResponse("GetProfileList", false, 207, "OBS is not ready.")]);
            }

            return Task.CompletedTask;
        }

        public Task ConfigureSegmentedRecordingAsync(
            string recordingDirectory,
            int segmentMinutes,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ConfigureAudioAsync(
            AudioRoutingProfile profile,
            MicrophoneProcessingSettings microphoneSettings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ConfigureGameCaptureAsync(
            GameCaptureTarget target,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ObsRecordingStatus> GetRecordingStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ObsRecordingStatus(false, false, TimeSpan.Zero, 0));

        public Task StartRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task<string?> StopRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Stop");
            return Task.FromResult(probePath);
        }
    }
}
