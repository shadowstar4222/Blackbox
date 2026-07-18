using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class AutomaticCaptureServiceTests
{
    [Fact]
    public async Task Canceled_enable_does_not_mutate_capture_state()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var detector = new CountingDetector();
        var preferenceStore = new InMemoryPreferenceStore();
        using var coordinator = CreateCoordinator();
        using var controller = CreateController(root, coordinator);
        var service = CreateService(detector, controller, preferenceStore);
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SetEnabledAsync(true, cancellation.Token));

            Assert.False(service.IsEnabled);
            Assert.False(preferenceStore.WasEnabled);
            Assert.Equal(0, detector.Calls);
        }
        finally
        {
            await service.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Enabling_caller_cancellation_does_not_stop_the_background_detector()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var detector = new CountingDetector();
        using var coordinator = CreateCoordinator();
        using var controller = CreateController(root, coordinator);
        var service = CreateService(detector, controller, new InMemoryPreferenceStore());
        try
        {
            using var callerCancellation = new CancellationTokenSource();
            await service.SetEnabledAsync(true, callerCancellation.Token);
            await detector.FirstDetection.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await callerCancellation.CancelAsync();
            var callsAfterCancellation = detector.Calls;

            await WaitUntilAsync(
                () => detector.Calls > callsAfterCancellation,
                TimeSpan.FromSeconds(2));

            Assert.True(service.IsEnabled);
            await service.SetEnabledAsync(false);
            Assert.False(service.IsEnabled);
        }
        finally
        {
            await service.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The background detector did not continue running.");
            }

            await Task.Delay(10);
        }
    }

    private static RecordingCoordinator CreateCoordinator() => new(
        new NoOpObsController(),
        new NoOpMicrophoneController(),
        new InMemoryMicrophoneStore(),
        new InMemorySegmentRepository(),
        new NoOpMicrophoneMonitor(),
        NullLogger<RecordingCoordinator>.Instance);

    private static AutomaticCaptureController CreateController(
        string recordingLocation,
        RecordingCoordinator coordinator) => new(
            new NoOpObsController(),
            coordinator,
            new RecordingSettings { RecordingLocation = recordingLocation },
            new FixedClock(DateTimeOffset.Parse("2026-07-18T12:00:00Z")),
            CreateOptions(),
            NullLogger<AutomaticCaptureController>.Instance);

    private static AutomaticCaptureService CreateService(
        IGameProcessDetector detector,
        AutomaticCaptureController controller,
        IAutomaticCapturePreferenceStore preferenceStore) => new(
            detector,
            controller,
            preferenceStore,
            CreateOptions(),
            NullLogger<AutomaticCaptureService>.Instance);

    private static AutomaticCaptureOptions CreateOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        RequiredPositiveDetections = 1,
        CaptureSettleDelay = TimeSpan.Zero,
        StopGracePeriod = TimeSpan.Zero
    };

    private sealed class CountingDetector : IGameProcessDetector
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstDetection { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<GameCaptureTarget?> DetectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            FirstDetection.TrySetResult();
            return Task.FromResult<GameCaptureTarget?>(null);
        }
    }

    private sealed class InMemoryPreferenceStore : IAutomaticCapturePreferenceStore
    {
        public bool WasEnabled { get; private set; }
        public void Save(bool enabled) => WasEnabled = enabled;
    }

    private sealed class InMemoryMicrophoneStore : IMicrophoneConfigurationStore
    {
        public MicrophoneConfiguration Current { get; private set; } = new();
        public void Save(MicrophoneConfiguration configuration) => Current = configuration;
    }

    private sealed class NoOpMicrophoneMonitor : IMicrophoneDeviceMonitor
    {
        public MicrophoneDeviceStatus CurrentStatus { get; } =
            new("default", MicrophoneConnectionState.Connected, DateTimeOffset.UtcNow);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpMicrophoneController : IObsMicrophoneController
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

    private sealed class NoOpObsController : IObsController
    {
        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConfigureAudioAsync(
            AudioRoutingProfile profile,
            MicrophoneProcessingSettings microphoneSettings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConfigureGameCaptureAsync(
            GameCaptureTarget target,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshGameCaptureAsync(
            GameCaptureTarget target,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ObsRecordingStatus> GetRecordingStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ObsRecordingStatus(false, false, TimeSpan.Zero, 0));
        public Task StartRecordingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> StopRecordingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
