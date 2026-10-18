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
        Assert.Equal(["Launch", "Configure:2", "ConfigureAudio:5:1", "Start"], obs.Calls);
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

    [Fact]
    public async Task TryStartAsync_applies_the_effective_game_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var obs = new RecordingObsController();
        var microphone = new RecordingMicrophoneController();
        var coordinator = new RecordingCoordinator(
            obs,
            microphone,
            new InMemoryMicrophoneConfigurationStore(),
            new InMemorySegmentRepository(),
            new RecordingMicrophoneMonitor(),
            NullLogger<RecordingCoordinator>.Instance);
        var effective = new EffectiveGameProfileSettings(
            "Example",
            new RecordingQualitySettings
            {
                Resolution = RecordingResolution.QuadHd1440,
                FramesPerSecond = 120,
                AudioBitrateKbps = 320
            },
            new RecordingSettings
            {
                RecordingLocation = "ignored",
                SegmentDurationMinutes = 5,
                MaximumStorageGigabytes = 250,
                MaximumRetainedDuration = TimeSpan.FromDays(7)
            },
            new MicrophoneProcessingSettings { InputGainDb = 4 },
            RecordingEncoderPreference.Hardware,
            RecordingQualityPreset.Quality,
            TimeSpan.Zero,
            CaptureVoiceChat: false,
            CaptureMicrophone: false,
            Crop: null,
            new GameExportPreset(),
            new GameNotificationPreferences(),
            new GameInputControls(),
            new AutomaticMarkerSettings(),
            new HashSet<string>());

        try
        {
            await coordinator.TryStartAsync(
                new RecordingSettings { RecordingLocation = root },
                null,
                effective);

            Assert.Contains("Configure:5", obs.Calls);
            Assert.Contains("ConfigureAudio:5:0", obs.Calls);
            Assert.False(microphone.Configured);
            Assert.True(microphone.Muted);
        }
        finally
        {
            await coordinator.TryStopAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Native_recording_can_start_without_a_microphone_and_reports_worker_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var native = new NativeTestController();
        var mic = new RecordingMicrophoneController();
        var monitor = new RecordingMicrophoneMonitor();
        using var coordinator = new RecordingCoordinator(native, mic,
            new InMemoryMicrophoneConfigurationStore(), new InMemorySegmentRepository(), monitor,
            NullLogger<RecordingCoordinator>.Instance);
        var states = new List<bool>();
        coordinator.RecordingStateChanged += states.Add;
        try
        {
            await coordinator.StartAsync(new RecordingSettings { RecordingLocation = root });
            Assert.True(coordinator.IsRecording);
            Assert.False(mic.Configured);
            Assert.True(mic.Muted);
            Assert.False(monitor.Started);
            native.Fail();
            Assert.False(coordinator.IsRecording);
            Assert.Equal(new[] { true, false }, states);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class NativeTestController : RecordingObsController, INativeRecorderController
    {
        public GameCaptureTarget? Target => null;
        public string Description => "Test native recorder";
        public event Action<string>? RecordingFailed;
        public event Action? TargetClosed;
        public void Fail() => RecordingFailed?.Invoke("Encoder disconnected");
        public void CloseTarget() => TargetClosed?.Invoke();
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
        public bool Muted { get; private set; }
        public MicrophoneProcessingSettings? LastProcessing { get; private set; }

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
            LastProcessing = processingSettings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(
            TimeSpan duration,
            IProgress<AudioLevelSnapshot>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetProcessingEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        {
            Muted = muted;
            return Task.CompletedTask;
        }

        public Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private class RecordingObsController : IObsController
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
            Calls.Add($"ConfigureAudio:{profile.Tracks.Count}:{profile.ApplicationAssignments.Count}");
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
