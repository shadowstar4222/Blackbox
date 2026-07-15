using Blackbox.Domain;

namespace Blackbox.Recording;

public sealed class MicrophoneCalibrationAnalyzer
{
    public MicrophoneCalibrationMeasurement Measure(
        MicrophoneCalibrationPhase phase,
        IReadOnlyList<AudioLevelSnapshot> snapshots)
    {
        var usable = snapshots
            .Where(static snapshot => double.IsFinite(snapshot.PeakDb) && double.IsFinite(snapshot.RmsDb))
            .ToArray();
        if (usable.Length == 0)
        {
            throw new InvalidOperationException("No microphone level samples were captured.");
        }

        var peakDb = usable.Max(static snapshot => snapshot.PeakDb);
        var averageRmsLinear = usable.Average(static snapshot => DbToLinear(snapshot.RmsDb));
        return new MicrophoneCalibrationMeasurement(
            phase,
            peakDb,
            LinearToDb(averageRmsLinear),
            usable.Length);
    }

    public MicrophoneCalibrationResult Analyze(
        MicrophoneCalibrationMeasurement backgroundNoise,
        MicrophoneCalibrationMeasurement normalVoice,
        MicrophoneCalibrationMeasurement loudVoice)
    {
        EnsurePhase(backgroundNoise, MicrophoneCalibrationPhase.BackgroundNoise);
        EnsurePhase(normalVoice, MicrophoneCalibrationPhase.NormalVoice);
        EnsurePhase(loudVoice, MicrophoneCalibrationPhase.LoudVoice);

        var recommendedGain = Math.Clamp(-9 - normalVoice.PeakDb, -12, 12);
        recommendedGain = Math.Clamp(
            Math.Min(recommendedGain, -1 - loudVoice.PeakDb),
            -12,
            12);
        var expanderThreshold = Math.Clamp(
            Math.Min(backgroundNoise.RmsDb + 6, normalVoice.RmsDb - 8),
            -60,
            -20);
        var compressorThreshold = Math.Clamp(
            normalVoice.PeakDb + recommendedGain - 6,
            -30,
            -8);

        return new MicrophoneCalibrationResult(
            backgroundNoise,
            normalVoice,
            loudVoice,
            Math.Round(recommendedGain, 1),
            Math.Round(expanderThreshold, 1),
            Math.Round(compressorThreshold, 1),
            loudVoice.PeakDb >= -0.5,
            loudVoice.PeakDb - normalVoice.PeakDb < 3);
    }

    private static void EnsurePhase(
        MicrophoneCalibrationMeasurement measurement,
        MicrophoneCalibrationPhase expected)
    {
        if (measurement.Phase != expected)
        {
            throw new ArgumentException($"Expected a {expected} measurement.", nameof(measurement));
        }
    }

    private static double DbToLinear(double value) => Math.Pow(10, value / 20);

    private static double LinearToDb(double value) => 20 * Math.Log10(value);
}
