using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RecordingGuardianServiceTests
{
    [Fact]
    public async Task CheckNow_repairs_confirmed_black_capture_without_stopping_session()
    {
        var target = Target();
        var context = new GuardianContext(target, true);
        var repairer = new Repairer();
        var health = new HealthClient(
            Sample(previewByte: 0),
            Sample(previewByte: 0));
        await using var guardian = CreateGuardian(
            health,
            context,
            repairer,
            options => options with { BlackFrameConfirmationSamples = 2 });

        await guardian.CheckNowAsync();
        await guardian.CheckNowAsync();

        Assert.Single(repairer.Actions);
        Assert.Equal(RecordingGuardianRepairAction.RefreshCapture, repairer.Actions[0].Action);
        Assert.Same(target, repairer.Actions[0].Target);
        Assert.True(context.IsRecording);
        Assert.Contains(
            guardian.History,
            entry => entry.Action?.Contains("Rebind", StringComparison.Ordinal) == true &&
                     entry.Result?.Contains("accepted", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task CheckNow_caps_repeated_repairs_and_escalates()
    {
        var repairer = new Repairer();
        var health = new HealthClient(
            Sample(sourceActive: false),
            Sample(sourceActive: false),
            Sample(sourceActive: false));
        await using var guardian = CreateGuardian(
            health,
            new GuardianContext(Target(), true),
            repairer,
            options => options with
            {
                MaximumRepairAttempts = 2,
                RepairCooldown = TimeSpan.Zero
            });

        await guardian.CheckNowAsync();
        await guardian.CheckNowAsync();
        await guardian.CheckNowAsync();

        Assert.Equal(2, repairer.Actions.Count);
        Assert.Equal(RecordingGuardianState.Faulted, guardian.Snapshot.State);
        Assert.Contains("exhausted", guardian.Snapshot.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckNow_restarts_unexpectedly_stopped_recording_output()
    {
        var repairer = new Repairer();
        var health = new HealthClient(Sample(recordingOutputActive: false));
        await using var guardian = CreateGuardian(
            health,
            new GuardianContext(Target(), true),
            repairer);

        await guardian.CheckNowAsync();

        var repair = Assert.Single(repairer.Actions);
        Assert.Equal(RecordingGuardianRepairAction.RestartRecordingOutput, repair.Action);
    }

    [Fact]
    public async Task CheckNow_escalates_repeated_probe_failures_without_repairing()
    {
        var repairer = new Repairer();
        var health = new HealthClient(
            new IOException("OBS disconnected"),
            new IOException("OBS disconnected"));
        await using var guardian = CreateGuardian(
            health,
            new GuardianContext(Target(), true),
            repairer,
            options => options with { ObsUnavailableConfirmationSamples = 2 });

        await guardian.CheckNowAsync();
        Assert.Equal(RecordingGuardianState.Warning, guardian.Snapshot.State);
        await guardian.CheckNowAsync();

        Assert.Equal(RecordingGuardianState.Faulted, guardian.Snapshot.State);
        Assert.Empty(repairer.Actions);
    }

    [Fact]
    public async Task CheckNow_recovers_after_obs_reconnect_and_clears_fault_state()
    {
        var health = new HealthClient(
            new IOException("OBS restarting"),
            new IOException("OBS restarting"),
            Sample(previewByte: 210));
        await using var guardian = CreateGuardian(
            health,
            new GuardianContext(Target(), true),
            new Repairer(),
            options => options with { ObsUnavailableConfirmationSamples = 2 });

        await guardian.CheckNowAsync();
        await guardian.CheckNowAsync();
        Assert.Equal(RecordingGuardianState.Faulted, guardian.Snapshot.State);

        await guardian.CheckNowAsync();

        Assert.Equal(RecordingGuardianState.Healthy, guardian.Snapshot.State);
        Assert.Equal(RecordingGuardianIssue.None, guardian.Snapshot.Issue);
    }

    [Fact]
    public async Task CheckNow_waits_without_contacting_obs_when_no_session_or_target_exists()
    {
        var health = new HealthClient();
        await using var guardian = CreateGuardian(
            health,
            new GuardianContext(null, false),
            new Repairer());

        await guardian.CheckNowAsync();

        Assert.Equal(RecordingGuardianState.Waiting, guardian.Snapshot.State);
        Assert.Equal(0, health.CaptureCount);
    }

    [Fact]
    public async Task CheckNow_does_not_probe_or_repair_while_recording_is_stopping()
    {
        var health = new HealthClient(Sample(recordingOutputActive: false));
        var context = new GuardianContext(Target(), true)
        {
            IsRecordingTransitioning = true
        };
        var repairer = new Repairer();
        await using var guardian = CreateGuardian(health, context, repairer);

        await guardian.CheckNowAsync();

        Assert.Equal(RecordingGuardianState.Waiting, guardian.Snapshot.State);
        Assert.Equal(0, health.CaptureCount);
        Assert.Empty(repairer.Actions);
    }

    private static RecordingGuardianService CreateGuardian(
        HealthClient health,
        GuardianContext context,
        Repairer repairer,
        Func<RecordingGuardianOptions, RecordingGuardianOptions>? customize = null)
    {
        var options = new RecordingGuardianOptions
        {
            PollInterval = TimeSpan.FromHours(1),
            RepairCooldown = TimeSpan.Zero,
            FrozenFrameConfirmationSamples = 10,
            SilentAudioConfirmationSamples = 10
        };
        options = customize?.Invoke(options) ?? options;
        return new RecordingGuardianService(
            health,
            new FrameAnalyzer(),
            context,
            repairer,
            options,
            new FixedClock(DateTimeOffset.Parse("2026-07-25T12:00:00Z")),
            NullLogger<RecordingGuardianService>.Instance);
    }

    private static GameCaptureTarget Target() => new(
        42,
        @"C:\Games\Example\game.exe",
        "game",
        "Example Game",
        "Example Game:game.exe",
        GameDetectionSource.ForegroundWindow);

    private static ObsCaptureHealthSample Sample(
        bool sourceActive = true,
        bool recordingOutputActive = true,
        byte previewByte = 255)
    {
        var timestamp = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        return new ObsCaptureHealthSample(
            timestamp,
            true,
            sourceActive,
            true,
            "Example Game:game.exe",
            ObsGameCaptureSettings.SpecificWindowMode,
            ObsGameCaptureSettings.WindowTitleMustMatchPriority,
            1920,
            1080,
            60,
            recordingOutputActive,
            -12,
            5,
            new ObsPreviewFrame([previewByte], "test/raw", timestamp),
            null);
    }

    private sealed class FrameAnalyzer : IRecordingFrameAnalyzer
    {
        public RecordingFrameAnalysis Analyze(ObsPreviewFrame frame) =>
            frame.ImageBytes[0] == 0
                ? new RecordingFrameAnalysis(480, 270, 0, 1, "BLACK")
                : new RecordingFrameAnalysis(480, 270, 0.5, 0.02, Convert.ToHexString(frame.ImageBytes));
    }

    private sealed class GuardianContext(
        GameCaptureTarget? target,
        bool isRecording) : IRecordingGuardianContext
    {
        public bool IsRecording { get; } = isRecording;
        public bool IsRecordingTransitioning { get; set; }
        public bool IsAutomaticCaptureEnabled => Target is not null;
        public GameCaptureTarget? Target { get; } = target;
    }

    private sealed class Repairer : IRecordingGuardianRepairer
    {
        public List<(RecordingGuardianRepairAction Action, GameCaptureTarget? Target)> Actions { get; } = [];

        public Task RepairAsync(
            RecordingGuardianRepairAction action,
            GameCaptureTarget? target,
            CancellationToken cancellationToken = default)
        {
            Actions.Add((action, target));
            return Task.CompletedTask;
        }
    }

    private sealed class HealthClient(params object[] results) : IObsCaptureHealthClient
    {
        private readonly Queue<object> _results = new(results);
        public int CaptureCount { get; private set; }

        public Task<ObsCaptureHealthSample> CaptureAsync(
            GameCaptureTarget? target,
            bool includePreview,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            var result = _results.Dequeue();
            return result is Exception exception
                ? Task.FromException<ObsCaptureHealthSample>(exception)
                : Task.FromResult((ObsCaptureHealthSample)result);
        }
    }
}
