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

    private static ObsAutoSetupService CreateService(
        IObsPortableProvisioner provisioner,
        IObsConnectionSettingsProvider provider,
        IObsController controller) =>
        new(
            provisioner,
            provider,
            controller,
            new ObsSetupPlanner(),
            new ObsOnboardingOptions
            {
                ConnectionAttempts = 1,
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

    private sealed class SetupObsController(ObsConnectionStatus status, string? probePath) : IObsController
    {
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
