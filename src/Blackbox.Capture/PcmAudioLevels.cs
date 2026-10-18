using System.Buffers.Binary;
using NAudio.Wave;
using NAudio.Dmo;

namespace Blackbox.Capture;

public static class PcmAudioLevels
{
    public static (double PeakDb, double RmsDb) Measure(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        var floatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extended && extended.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        var pcm = format.Encoding == WaveFormatEncoding.Pcm ||
            format is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
        var width = format.BitsPerSample / 8;
        if (!(floatingPoint && width == 4) && !(pcm && width is 1 or 2 or 3 or 4))
            throw new NotSupportedException($"Unsupported microphone sample format: {format}.");
        double peak = 0, sum = 0;
        var samples = bytes.Length / width;
        for (var i = 0; i < samples; i++)
        {
            var sampleBytes = bytes.Slice(i * width, width);
            double sample = floatingPoint ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sampleBytes)) : width switch
            {
                1 => (sampleBytes[0] - 128) / 128d,
                2 => BinaryPrimitives.ReadInt16LittleEndian(sampleBytes) / 32768d,
                3 => ((sampleBytes[0] | sampleBytes[1] << 8 | sampleBytes[2] << 16) << 8 >> 8) / 8388608d,
                _ => BinaryPrimitives.ReadInt32LittleEndian(sampleBytes) / 2147483648d
            };
            if (!double.IsFinite(sample)) sample = 0;
            sample = Math.Clamp(sample, -1, 1);
            peak = Math.Max(peak, Math.Abs(sample));
            sum += sample * sample;
        }
        return (Decibels(peak), Decibels(samples == 0 ? 0 : Math.Sqrt(sum / samples)));
    }

    private static double Decibels(double amplitude) => amplitude <= 0 ? -100 : Math.Max(-100, 20 * Math.Log10(amplitude));
}
