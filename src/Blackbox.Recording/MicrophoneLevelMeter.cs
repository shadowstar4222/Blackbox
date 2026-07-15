using Blackbox.Domain;

namespace Blackbox.Recording;

public sealed class MicrophoneLevelMeter(IClock clock)
{
    public AudioLevelSnapshot CreateSnapshot(string sourceName, ReadOnlySpan<float> samples)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            throw new ArgumentException("Source name is required.", nameof(sourceName));
        }

        if (samples.IsEmpty)
        {
            return new AudioLevelSnapshot(sourceName, double.NegativeInfinity, double.NegativeInfinity, clock.UtcNow);
        }

        var peak = 0d;
        var sumSquares = 0d;
        foreach (var sample in samples)
        {
            var absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            sumSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length);
        return new AudioLevelSnapshot(sourceName, LinearToDb(peak), LinearToDb(rms), clock.UtcNow);
    }

    private static double LinearToDb(double value)
    {
        return value <= 0 ? double.NegativeInfinity : 20 * Math.Log10(value);
    }
}
