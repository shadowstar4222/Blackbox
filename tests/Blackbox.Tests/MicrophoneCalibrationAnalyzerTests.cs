using Blackbox.Domain;
using Blackbox.Recording;

namespace Blackbox.Tests;

public sealed class MicrophoneCalibrationAnalyzerTests
{
    private readonly MicrophoneCalibrationAnalyzer _analyzer = new();

    [Fact]
    public void Measure_uses_highest_peak_and_linear_average_level()
    {
        var now = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        AudioLevelSnapshot[] snapshots =
        [
            new("Mic", -18, -30, now),
            new("Mic", -12, -24, now.AddMilliseconds(50))
        ];

        var measurement = _analyzer.Measure(MicrophoneCalibrationPhase.NormalVoice, snapshots);

        Assert.Equal(-12, measurement.PeakDb);
        Assert.InRange(measurement.RmsDb, -26.5, -26.4);
        Assert.Equal(2, measurement.SampleCount);
    }

    [Fact]
    public void Analyze_protects_loud_voice_headroom_and_places_thresholds_between_noise_and_voice()
    {
        var result = _analyzer.Analyze(
            Measurement(MicrophoneCalibrationPhase.BackgroundNoise, -42, -48),
            Measurement(MicrophoneCalibrationPhase.NormalVoice, -12, -24),
            Measurement(MicrophoneCalibrationPhase.LoudVoice, -2, -10));

        Assert.Equal(1, result.RecommendedGainDb);
        Assert.Equal(-42, result.RecommendedExpanderThresholdDb);
        Assert.Equal(-17, result.RecommendedCompressorThresholdDb);
        Assert.False(result.ClippingDetected);
        Assert.False(result.AutomaticGainControlSuspected);
    }

    [Fact]
    public void Analyze_flags_clipping_and_suspiciously_flat_loudness()
    {
        var result = _analyzer.Analyze(
            Measurement(MicrophoneCalibrationPhase.BackgroundNoise, -40, -48),
            Measurement(MicrophoneCalibrationPhase.NormalVoice, -2, -12),
            Measurement(MicrophoneCalibrationPhase.LoudVoice, -0.2, -10));

        Assert.True(result.ClippingDetected);
        Assert.True(result.AutomaticGainControlSuspected);
    }

    private static MicrophoneCalibrationMeasurement Measurement(
        MicrophoneCalibrationPhase phase,
        double peak,
        double rms) => new(phase, peak, rms, 60);
}
