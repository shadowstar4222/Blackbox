using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class AutomaticCaptureControllerTests
{
    [Fact]
    public async Task ProcessDetectionAsync_starts_after_confirmation_and_stops_after_grace_period()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));
            var obs = new AutomaticCaptureObsController();
            var coordinator = CreateCoordinator(obs);
            var controller = CreateController(root, obs, coordinator, clock);
            var target = CreateTarget();

            controller.Enable();
            await controller.ProcessDetectionAsync(target);

            Assert.Equal(AutomaticCaptureState.Confirming, controller.Status.State);
            Assert.False(coordinator.IsRecording);

            await controller.ProcessDetectionAsync(target);

            Assert.Equal(AutomaticCaptureState.Recording, controller.Status.State);
            Assert.True(coordinator.IsRecording);
            Assert.Contains("ConfigureGame:Example.exe", obs.Calls);
            Assert.Contains("Start", obs.Calls);
            Assert.Equal(
                RecordingDirectoryLayout.GetSessionDirectory(
                    root,
                    target.Title,
                    clock.UtcNow),
                Assert.Single(obs.RecordingDirectories));
            Assert.Equal(target, Assert.Single(obs.SegmentCaptureTargets));

            await controller.ProcessDetectionAsync(null);
            Assert.True(coordinator.IsRecording);

            clock.Advance(TimeSpan.FromSeconds(11));
            await controller.ProcessDetectionAsync(null);

            Assert.False(coordinator.IsRecording);
            Assert.Equal(AutomaticCaptureState.Watching, controller.Status.State);
            Assert.Contains("Stop", obs.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DisableAsync_does_not_stop_a_manual_recording()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));
            var obs = new AutomaticCaptureObsController();
            var coordinator = CreateCoordinator(obs);
            await coordinator.StartAsync(new RecordingSettings { RecordingLocation = root });
            var controller = CreateController(root, obs, coordinator, clock);

            controller.Enable();
            await controller.ProcessDetectionAsync(CreateTarget());
            await controller.ProcessDetectionAsync(CreateTarget());
            await controller.DisableAsync();

            Assert.True(coordinator.IsRecording);
            Assert.DoesNotContain("Stop", obs.Calls);
            await coordinator.StopAsync();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProcessDetectionAsync_restarts_after_rebinding_when_the_active_game_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));
            var obs = new AutomaticCaptureObsController();
            var coordinator = CreateCoordinator(obs);
            var controller = CreateController(root, obs, coordinator, clock);
            var first = CreateTarget();
            var second = CreateTarget() with
            {
                ProcessId = 84,
                ExecutablePath = "C:\\Games\\Second.exe",
                ExecutableName = "Second.exe",
                Title = "Second Game",
                ObsWindowIdentifier = "Second Game:WindowClass:Second.exe"
            };

            controller.Enable();
            await controller.ProcessDetectionAsync(first);
            await controller.ProcessDetectionAsync(first);
            var callCountBeforeSwitch = obs.Calls.Count;

            await controller.ProcessDetectionAsync(second);
            await controller.ProcessDetectionAsync(second);

            var switchCalls = obs.Calls.Skip(callCountBeforeSwitch).ToArray();
            Assert.Equal(
                ["Stop", "ConfigureGame:Second.exe", "Launch", "ConfigureSegments", "ConfigureAudio", "Start"],
                switchCalls);
            Assert.True(coordinator.IsRecording);
            Assert.Equal("Second Game", controller.Status.Target?.Title);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProcessDetectionAsync_rebinds_when_same_executable_replaces_its_capture_window()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));
            var obs = new AutomaticCaptureObsController();
            var coordinator = CreateCoordinator(obs);
            var controller = CreateController(root, obs, coordinator, clock);
            var launcherWindow = CreateTarget() with
            {
                Title = "Example Launcher",
                ObsWindowIdentifier = "Example Launcher:LauncherWindow:Example.exe"
            };
            var gameWindow = launcherWindow with
            {
                Title = "Example Game",
                ObsWindowIdentifier = "Example Game:GameWindow:Example.exe"
            };

            controller.Enable();
            await controller.ProcessDetectionAsync(launcherWindow);
            await controller.ProcessDetectionAsync(launcherWindow);
            var callCountBeforeHandoff = obs.Calls.Count;

            await controller.ProcessDetectionAsync(gameWindow);
            await controller.ProcessDetectionAsync(gameWindow);

            Assert.Equal(
                ["Stop", "ConfigureGame:Example.exe", "Launch", "ConfigureSegments", "ConfigureAudio", "Start"],
                obs.Calls.Skip(callCountBeforeHandoff).ToArray());
            Assert.Equal("Example Game", controller.Status.Target?.Title);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProcessDetectionAsync_reframes_a_resized_window_without_restarting_recording()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));
            var obs = new AutomaticCaptureObsController();
            var coordinator = CreateCoordinator(obs);
            var controller = CreateController(root, obs, coordinator, clock);
            var target = CreateTarget();

            controller.Enable();
            await controller.ProcessDetectionAsync(target);
            await controller.ProcessDetectionAsync(target);
            var callCountBeforeResize = obs.Calls.Count;

            await controller.ProcessDetectionAsync(target with
            {
                WindowWidth = 1600,
                WindowHeight = 900
            });

            Assert.Equal(
                ["RefreshGame:Example.exe"],
                obs.Calls.Skip(callCountBeforeResize).ToArray());
            Assert.True(coordinator.IsRecording);
            Assert.Equal(1600, controller.Status.Target?.WindowWidth);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AdoptRecordingOwnership_stops_surviving_recording_when_no_game_returns()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));
            var obs = new AutomaticCaptureObsController();
            var coordinator = CreateCoordinator(obs);
            await coordinator.StartAsync(new RecordingSettings { RecordingLocation = root });
            var controller = CreateController(root, obs, coordinator, clock);
            controller.Enable();
            controller.AdoptRecordingOwnership();

            await controller.ProcessDetectionAsync(null);
            Assert.True(coordinator.IsRecording);

            clock.Advance(TimeSpan.FromSeconds(11));
            await controller.ProcessDetectionAsync(null);

            Assert.False(coordinator.IsRecording);
            Assert.Contains("Stop", obs.Calls);
            Assert.Contains("no remembered game", controller.Status.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static AutomaticCaptureController CreateController(
        string recordingPath,
        IObsController obs,
        RecordingCoordinator coordinator,
        IClock clock) =>
        new(
            obs,
            coordinator,
            new RecordingSettings { RecordingLocation = recordingPath },
            clock,
            new AutomaticCaptureOptions
            {
                PollInterval = TimeSpan.FromHours(1),
                RequiredPositiveDetections = 2,
                CaptureSettleDelay = TimeSpan.Zero,
                StopGracePeriod = TimeSpan.FromSeconds(10)
            },
            NullLogger<AutomaticCaptureController>.Instance);

    private static RecordingCoordinator CreateCoordinator(IObsController obs) =>
        new(
            obs,
            new AutomaticCaptureMicrophoneController(),
            new AutomaticCaptureMicrophoneStore(),
            new InMemorySegmentRepository(),
            new AutomaticCaptureMicrophoneMonitor(),
            NullLogger<RecordingCoordinator>.Instance);

    private static GameCaptureTarget CreateTarget() => new(
        42,
        "C:\\Steam\\steamapps\\common\\Example\\Example.exe",
        "Example.exe",
        "Example Game",
        "Example Game:ExampleWindow:Example.exe",
        GameDetectionSource.ForegroundWindow | GameDetectionSource.SteamLibrary);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class AutomaticCaptureMicrophoneStore : IMicrophoneConfigurationStore
    {
        public MicrophoneConfiguration Current { get; private set; } = new();

        public void Save(MicrophoneConfiguration configuration) => Current = configuration;
    }

    private sealed class AutomaticCaptureMicrophoneMonitor : IMicrophoneDeviceMonitor
    {
        public MicrophoneDeviceStatus CurrentStatus { get; } = new(
            "default",
            MicrophoneConnectionState.Connected,
            DateTimeOffset.UtcNow);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AutomaticCaptureMicrophoneController : IObsMicrophoneController
    {
        public Task<IReadOnlyList<MicrophoneDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MicrophoneDevice>>([]);

        public Task<MicrophoneDeviceStatus> GetDeviceStatusAsync(
            string deviceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ConfigureAsync(
            MicrophoneDevice device,
            MicrophoneProcessingSettings processingSettings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AudioLevelSnapshot>> CaptureLevelsAsync(
            TimeSpan duration,
            IProgress<AudioLevelSnapshot>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetProcessingEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class AutomaticCaptureObsController : IObsController
    {
        public List<string> Calls { get; } = [];
        public List<string> RecordingDirectories { get; } = [];
        public List<GameCaptureTarget?> SegmentCaptureTargets { get; } = [];

        public Task LaunchAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Launch");
            return Task.CompletedTask;
        }

        public Task<ObsConnectionStatus> TestConnectionAsync(
            ObsConnectionSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ObsConnectionStatus.Connected());

        public Task ApplySetupPlanAsync(
            ObsConnectionSettings settings,
            ObsSetupPlan plan,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ConfigureSegmentedRecordingAsync(
            string recordingDirectory,
            int segmentMinutes,
            GameCaptureTarget? captureTarget = null,
            CancellationToken cancellationToken = default)
        {
            RecordingDirectories.Add(recordingDirectory);
            SegmentCaptureTargets.Add(captureTarget);
            Calls.Add("ConfigureSegments");
            return Task.CompletedTask;
        }

        public Task ConfigureAudioAsync(
            AudioRoutingProfile profile,
            MicrophoneProcessingSettings microphoneSettings,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("ConfigureAudio");
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
            return Task.FromResult<string?>(null);
        }
    }
}
