using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RecordingCoordinatorTests
{
    [Fact]
    public async Task StartAsync_initializes_database_and_starts_obs_in_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var repository = new InMemorySegmentRepository();
        var obs = new RecordingObsController();
        var microphone = new RecordingMicrophoneController();
        var monitor = new RecordingMicrophoneMonitor();
        var coordinator = new RecordingCoordinator(
            obs,
            microphone,
            new InMemoryMicrophoneConfigurationStore(),
            repository,
            monitor,
            NullLogger<RecordingCoordinator>.Instance);

        await coordinator.StartAsync(new RecordingSettings { RecordingLocation = root, SegmentDurationMinutes = 2 });

        Assert.True(repository.Initialized);
        Assert.Equal(["Launch", "Configure:2", "ConfigureAudio:5", "Start"], obs.Calls);
        Assert.True(microphone.Configured);
        Assert.True(monitor.Started);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task TryStartAndStopAsync_are_idempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var obs = new RecordingObsController();
        var coordinator = new RecordingCoordinator(
            obs,
            new RecordingMicrophoneController(),
            new InMemoryMicrophoneConfigurationStore(),
            new InMemorySegmentRepository(),
            new RecordingMicrophoneMonitor(),
            NullLogger<RecordingCoordinator>.Instance);

        try
        {
            Assert.True(await coordinator.TryStartAsync(new RecordingSettings { RecordingLocation = root }));
            Assert.False(await coordinator.TryStartAsync(new RecordingSettings { RecordingLocation = root }));
            Assert.True(await coordinator.TryStopAsync());
            Assert.False(await coordinator.TryStopAsync());

            Assert.Equal(1, obs.Calls.Count(static call => call == "Start"));
            Assert.Equal(1, obs.Calls.Count(static call => call == "Stop"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task TryAdoptExistingRecordingAsync_resumes_state_without_starting_obs_again()
    {
        var obs = new RecordingObsController { OutputActive = true };
        var monitor = new RecordingMicrophoneMonitor();
        var coordinator = new RecordingCoordinator(
            obs,
            new RecordingMicrophoneController(),
            new InMemoryMicrophoneConfigurationStore(),
            new InMemorySegmentRepository(),
            monitor,
            NullLogger<RecordingCoordinator>.Instance);

        Assert.True(await coordinator.TryAdoptExistingRecordingAsync());

        Assert.True(coordinator.IsRecording);
        Assert.True(monitor.Started);
        Assert.Equal(["GetRecordStatus"], obs.Calls);
    }

    private sealed class InMemoryMicrophoneConfigurationStore : IMicrophoneConfigurationStore
    {
        public MicrophoneConfiguration Current { get; private set; } = new();

        public void Save(MicrophoneConfiguration configuration) => Current = configuration;
    }

    private sealed class RecordingMicrophoneMonitor : IMicrophoneDeviceMonitor
    {
        public MicrophoneDeviceStatus CurrentStatus { get; } = new(
            "default",
            MicrophoneConnectionState.Connected,
            DateTimeOffset.UtcNow);

        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Started = false;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMicrophoneController : IObsMicrophoneController
    {
        public bool Configured { get; private set; }

        public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MicrophoneDevice>>([]);

        public Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(
            string deviceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ConfigureAsync(
            MicrophoneDevice device,
            MicrophoneProcessingSettings processingSettings,
            CancellationToken cancellationToken = default)
        {
            Configured = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(
            TimeSpan duration,
            IProgress<AudioLevelSnapshot>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetProcessingEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class RecordingObsController : IObsController
    {
        public List<string> Calls { get; } = [];
        public bool OutputActive { get; init; }

        public Task LaunchAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Launch");
            return Task.CompletedTask;
        }

        public Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add("TestConnection");
            return Task.FromResult(ObsConnectionStatus.Connected());
        }

        public Task ApplySetupPlanAsync(ObsConnectionSettings settings, ObsSetupPlan plan, CancellationToken cancellationToken = default)
        {
            Calls.Add($"ApplySetup:{plan.Sources.Count}");
            return Task.CompletedTask;
        }

        public Task ConfigureSegmentedRecordingAsync(
            string recordingDirectory,
            int segmentMinutes,
            GameCaptureTarget? captureTarget = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"Configure:{segmentMinutes}");
            return Task.CompletedTask;
        }

        public Task ConfigureAudioAsync(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings, CancellationToken cancellationToken = default)
        {
            Calls.Add($"ConfigureAudio:{profile.Tracks.Count}");
            return Task.CompletedTask;
        }

        public Task ConfigureGameCaptureAsync(
            GameCaptureTarget target,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"ConfigureGame:{target.ExecutableName}");
            return Task.CompletedTask;
        }

        public Task RefreshGameCaptureAsync(
            GameCaptureTarget target,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"RefreshGame:{target.ExecutableName}");
            return Task.CompletedTask;
        }

        public Task<ObsRecordingStatus> GetRecordingStatusAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("GetRecordStatus");
            return Task.FromResult(new ObsRecordingStatus(OutputActive, false, TimeSpan.FromMinutes(1), 1024));
        }

        public Task StartRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task<string?> StopRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Stop");
            return Task.FromResult<string?>(null);
        }
    }
}
