using Blackbox.Domain;
using Blackbox.Recording;

namespace Blackbox.Tests;

public sealed class AutomaticMarkerEvaluatorTests
{
    private readonly AutomaticMarkerEvaluator _evaluator = new();
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-07-25T20:00:00Z");

    [Fact]
    public void Evaluate_does_nothing_when_automatic_markers_are_disabled()
    {
        var decision = _evaluator.Evaluate(
            Signal(audioPeakDb: -2),
            null,
            null,
            new AutomaticMarkerSettings());

        Assert.Null(decision);
    }

    [Fact]
    public void Evaluate_explains_audio_spike_markers()
    {
        var decision = _evaluator.Evaluate(
            Signal(audioPeakDb: -5),
            null,
            null,
            new AutomaticMarkerSettings
            {
                Enabled = true,
                DetectAudioSpikes = true,
                AudioSpikeThresholdDb = -8
            });

        Assert.NotNull(decision);
        Assert.Equal(TimelineMarkerOrigin.AudioSpike, decision.Origin);
        Assert.Contains("-5.0 dB", decision.Reason);
        Assert.Contains("-8.0 dB", decision.Reason);
    }

    [Fact]
    public void Evaluate_detects_large_scene_changes_but_ignores_small_changes()
    {
        var settings = new AutomaticMarkerSettings
        {
            Enabled = true,
            DetectSceneChanges = true,
            SceneLuminanceDeltaThreshold = 0.25
        };
        var previous = Signal(meanLuminance: 0.2);

        Assert.Null(_evaluator.Evaluate(
            Signal(meanLuminance: 0.4),
            previous,
            null,
            settings));
        var decision = _evaluator.Evaluate(
            Signal(meanLuminance: 0.7),
            previous,
            null,
            settings);

        Assert.NotNull(decision);
        Assert.Equal(TimelineMarkerOrigin.SceneChange, decision.Origin);
        Assert.Contains("50", decision.Reason);
    }

    [Fact]
    public void Evaluate_honors_cooldown_and_input_rule_priority()
    {
        var settings = new AutomaticMarkerSettings
        {
            Enabled = true,
            DetectAudioSpikes = true,
            DetectInputRules = true,
            Cooldown = TimeSpan.FromSeconds(30)
        };
        var signal = Signal(audioPeakDb: -1, inputRule: "Boss defeated");

        Assert.Null(_evaluator.Evaluate(
            signal,
            null,
            _now.AddSeconds(-10),
            settings));
        var decision = _evaluator.Evaluate(
            signal,
            null,
            _now.AddSeconds(-31),
            settings);

        Assert.NotNull(decision);
        Assert.Equal(TimelineMarkerOrigin.InputRule, decision.Origin);
        Assert.Contains("Boss defeated", decision.Reason);
    }

    private AutomaticMarkerSignal Signal(
        double? audioPeakDb = null,
        double? meanLuminance = null,
        string? inputRule = null) =>
        new(_now, "Example Game", audioPeakDb, meanLuminance, inputRule);
}
