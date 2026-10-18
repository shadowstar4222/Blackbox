using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;

namespace Blackbox.Tests;

public sealed class RecordingGuardianEvaluatorTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-07-25T12:00:00Z");

    [Fact]
    public void Native_health_does_not_treat_unavailable_optional_meters_as_failed_capture()
    {
        var evaluator = CreateEvaluator();
        var sample = Sample() with { SupportsPreview = false, SupportsAudioMeter = false,
            GameAudioSampleCount = 0, GameAudioPeakDb = null };
        for (var index = 0; index < 20; index++)
        {
            var result = evaluator.Evaluate(sample, null, Target(), true);
            Assert.Equal(RecordingGuardianIssue.None, result.Issue);
            Assert.Equal(RecordingGuardianRepairAction.None, result.RepairAction);
        }
        var stopped = evaluator.Evaluate(sample with { RecordingOutputActive = false }, null, Target(), true);
        Assert.True(stopped.Issue.HasFlag(RecordingGuardianIssue.RecordingStopped));
    }

    [Fact]
    public void Evaluate_reports_healthy_exact_window_capture()
    {
        var evaluator = CreateEvaluator();

        var result = evaluator.Evaluate(
            Sample(),
            Frame("A"),
            Target(),
            blackboxIsRecording: true);

        Assert.Equal(RecordingGuardianIssue.None, result.Issue);
        Assert.Equal(RecordingGuardianRepairAction.None, result.RepairAction);
        Assert.Contains("healthy", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Healthy_native_sample_reports_verified_microphone_packets()
    {
        var evaluator = CreateEvaluator();
        var sample = Sample() with
        {
            MicrophoneCaptureEnabled = true,
            MicrophonePacketReceived = true,
            MicrophonePacketCount = 7
        };

        var result = evaluator.Evaluate(sample, Frame("A"), Target(), true);

        Assert.Contains("Microphone active", result.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7 packets", result.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Healthy_native_sample_makes_profile_disabled_microphone_explicit()
    {
        var evaluator = CreateEvaluator();

        var result = evaluator.Evaluate(Sample(), Frame("A"), Target(), true);

        Assert.Contains("disabled by the active recording profile", result.Evidence,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_requires_sustained_black_frames_before_repair()
    {
        var evaluator = CreateEvaluator(options => options with
        {
            BlackFrameConfirmationSamples = 2
        });
        var black = new RecordingFrameAnalysis(480, 270, 0.001, 0.999, "BLACK");

        var first = evaluator.Evaluate(Sample(), black, Target(), true);
        var second = evaluator.Evaluate(Sample(), black, Target(), true);

        Assert.False(first.Issue.HasFlag(RecordingGuardianIssue.BlackFrame));
        Assert.True(second.Issue.HasFlag(RecordingGuardianIssue.BlackFrame));
        Assert.Equal(RecordingGuardianRepairAction.RefreshCapture, second.RepairAction);
    }

    [Fact]
    public void Evaluate_ignores_short_static_frame_then_detects_sustained_freeze()
    {
        var evaluator = CreateEvaluator(options => options with
        {
            FrozenFrameConfirmationSamples = 3
        });

        var first = evaluator.Evaluate(Sample(), Frame("STATIC"), Target(), true);
        var second = evaluator.Evaluate(Sample(), Frame("STATIC"), Target(), true);
        var third = evaluator.Evaluate(Sample(), Frame("STATIC"), Target(), true);
        var fourth = evaluator.Evaluate(Sample(), Frame("STATIC"), Target(), true);

        Assert.False(first.Issue.HasFlag(RecordingGuardianIssue.FrozenFrame));
        Assert.False(second.Issue.HasFlag(RecordingGuardianIssue.FrozenFrame));
        Assert.False(third.Issue.HasFlag(RecordingGuardianIssue.FrozenFrame));
        Assert.True(fourth.Issue.HasFlag(RecordingGuardianIssue.FrozenFrame));
    }

    [Fact]
    public void Evaluate_requires_sustained_silence_when_game_audio_is_expected()
    {
        var evaluator = CreateEvaluator(options => options with
        {
            SilentAudioConfirmationSamples = 2
        });
        var silent = Sample(audioPeakDb: -80, audioSamples: 4);

        var first = evaluator.Evaluate(silent, Frame("A"), Target(), true);
        var second = evaluator.Evaluate(silent, Frame("B"), Target(), true);

        Assert.False(first.Issue.HasFlag(RecordingGuardianIssue.SilentGameAudio));
        Assert.True(second.Issue.HasFlag(RecordingGuardianIssue.SilentGameAudio));
        Assert.Equal(RecordingGuardianRepairAction.RefreshCapture, second.RepairAction);
    }

    [Fact]
    public void Evaluate_does_not_flag_silence_when_game_audio_is_excluded()
    {
        var evaluator = CreateEvaluator(options => options with
        {
            SilentAudioConfirmationSamples = 2
        });
        var target = Target() with { CaptureGameAudio = false };
        var silent = Sample(audioPeakDb: null, audioSamples: 0);

        evaluator.Evaluate(silent, Frame("A"), target, true);
        var result = evaluator.Evaluate(silent, Frame("B"), target, true);

        Assert.False(result.Issue.HasFlag(RecordingGuardianIssue.SilentGameAudio));
    }

    [Fact]
    public void Evaluate_repairs_wrong_window_binding()
    {
        var evaluator = CreateEvaluator();

        var result = evaluator.Evaluate(
            Sample(matchesTarget: false, boundWindow: "Other Window:other.exe"),
            Frame("A"),
            Target(),
            true);

        Assert.True(result.Issue.HasFlag(RecordingGuardianIssue.WrongWindow));
        Assert.Equal(RecordingGuardianRepairAction.RefreshCapture, result.RepairAction);
        Assert.Contains("Other Window", result.Evidence);
    }

    [Fact]
    public void Evaluate_marks_missing_source_as_operator_fault()
    {
        var evaluator = CreateEvaluator();

        var result = evaluator.Evaluate(
            Sample(sourceExists: false, sourceActive: false, preview: null),
            null,
            Target(),
            true);

        Assert.True(result.Issue.HasFlag(RecordingGuardianIssue.MissingSource));
        Assert.True(result.IsFault);
        Assert.Equal(RecordingGuardianRepairAction.None, result.RepairAction);
        Assert.Contains("Select an open window", result.Message);
    }

    [Fact]
    public void Evaluate_prioritizes_restarting_stopped_output_over_capture_refresh()
    {
        var evaluator = CreateEvaluator(options => options with
        {
            BlackFrameConfirmationSamples = 1
        });

        var result = evaluator.Evaluate(
            Sample(recordingOutputActive: false),
            new RecordingFrameAnalysis(480, 270, 0, 1, "BLACK"),
            Target(),
            true);

        Assert.True(result.Issue.HasFlag(RecordingGuardianIssue.RecordingStopped));
        Assert.True(result.Issue.HasFlag(RecordingGuardianIssue.BlackFrame));
        Assert.Equal(RecordingGuardianRepairAction.RestartRecordingOutput, result.RepairAction);
    }

    [Fact]
    public void Evaluate_unavailable_requires_confirmation()
    {
        var evaluator = CreateEvaluator(options => options with
        {
            ObsUnavailableConfirmationSamples = 2
        });

        var first = evaluator.EvaluateUnavailable(new IOException("connection reset"));
        var second = evaluator.EvaluateUnavailable(new IOException("connection reset"));

        Assert.False(first.IsFault);
        Assert.True(second.IsFault);
        Assert.True(second.Issue.HasFlag(RecordingGuardianIssue.ObsUnavailable));
    }

    [Fact]
    public void Healthy_sample_resets_intermittent_black_confirmation()
    {
        var evaluator = CreateEvaluator(options => options with
        {
            BlackFrameConfirmationSamples = 2
        });
        var black = new RecordingFrameAnalysis(480, 270, 0, 1, "BLACK");

        evaluator.Evaluate(Sample(), black, Target(), true);
        evaluator.Evaluate(Sample(), Frame("GOOD"), Target(), true);
        var result = evaluator.Evaluate(Sample(), black, Target(), true);

        Assert.False(result.Issue.HasFlag(RecordingGuardianIssue.BlackFrame));
    }

    [Fact]
    public void Rate_limited_native_preview_checks_do_not_erase_black_confirmation()
    {
        var evaluator = CreateEvaluator();
        var black = new RecordingFrameAnalysis(320, 180, 0, 1, "BLACK");
        evaluator.Evaluate(Sample(), black, Target(), true);
        for (var i = 0; i < 4; i++)
            evaluator.Evaluate(Sample() with { PreviewSampled = false }, null, Target(), true);
        var result = evaluator.Evaluate(Sample(), black, Target(), true);
        Assert.True(result.Issue.HasFlag(RecordingGuardianIssue.BlackFrame));
        Assert.False(result.Issue.HasFlag(RecordingGuardianIssue.PreviewUnavailable));
    }

    [Fact]
    public void Confirmed_native_black_warning_persists_until_fresh_video_and_does_not_offer_a_noop_repair()
    {
        var evaluator = CreateEvaluator();
        var sample = Sample() with { SupportsAutomaticCaptureRefresh = false };
        var black = new RecordingFrameAnalysis(320, 180, 0, 1, "BLACK");
        evaluator.Evaluate(sample, black, Target(), true);
        evaluator.Evaluate(sample, black, Target(), true);
        var held = evaluator.Evaluate(sample with { PreviewSampled = false }, null, Target(), true);
        Assert.True(held.Issue.HasFlag(RecordingGuardianIssue.BlackFrame));
        Assert.Equal(RecordingGuardianRepairAction.None, held.RepairAction);
        var recovered = evaluator.Evaluate(sample, Frame("GOOD"), Target(), true);
        Assert.False(recovered.Issue.HasFlag(RecordingGuardianIssue.BlackFrame));
    }

    private static RecordingGuardianEvaluator CreateEvaluator(
        Func<RecordingGuardianOptions, RecordingGuardianOptions>? customize = null)
    {
        var options = new RecordingGuardianOptions
        {
            FrozenFrameConfirmationSamples = 10,
            SilentAudioConfirmationSamples = 10
        };
        return new RecordingGuardianEvaluator(customize?.Invoke(options) ?? options);
    }

    private static RecordingFrameAnalysis Frame(string fingerprint) =>
        new(480, 270, 0.42, 0.05, fingerprint);

    private static GameCaptureTarget Target() => new(
        42,
        @"C:\Games\Example\game.exe",
        "game",
        "Example Game",
        "Example Game:game.exe",
        GameDetectionSource.ForegroundWindow,
        1920,
        1080,
        true);

    private static ObsCaptureHealthSample Sample(
        bool sourceExists = true,
        bool sourceActive = true,
        bool matchesTarget = true,
        string boundWindow = "Example Game:game.exe",
        bool recordingOutputActive = true,
        double? audioPeakDb = -12,
        int audioSamples = 5,
        ObsPreviewFrame? preview = null) => new(
            CapturedAt,
            sourceExists,
            sourceActive,
            matchesTarget,
            boundWindow,
            ObsGameCaptureSettings.SpecificWindowMode,
            ObsGameCaptureSettings.WindowTitleMustMatchPriority,
            1920,
            1080,
            60,
            recordingOutputActive,
            audioPeakDb,
            audioSamples,
            preview,
            sourceExists ? null : "GetInputSettings: source not found");
}
