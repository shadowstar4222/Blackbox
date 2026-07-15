namespace Blackbox.Export;

internal static class WaveformSampler
{
    public static IReadOnlyList<double> Sample(ReadOnlySpan<byte> pcmBytes, int bucketCount)
    {
        if (bucketCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount));
        }

        var sampleCount = pcmBytes.Length / 2;
        if (sampleCount == 0)
        {
            return new double[bucketCount];
        }

        var waveform = new double[bucketCount];
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = bucket * sampleCount / bucketCount;
            var end = Math.Max(start + 1, (bucket + 1) * sampleCount / bucketCount);
            short peak = 0;
            for (var index = start; index < Math.Min(end, sampleCount); index++)
            {
                var sample = BitConverter.ToInt16(pcmBytes.Slice(index * 2, 2));
                var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                peak = (short)Math.Max(peak, magnitude);
            }

            waveform[bucket] = peak / (double)short.MaxValue;
        }

        return waveform;
    }
}
