using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class WaveformSamplerTests
{
    [Fact]
    public void Sample_returns_normalized_peaks_for_each_bucket()
    {
        var samples = new short[] { 0, 16384, -32768, 8192 };
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var waveform = WaveformSampler.Sample(bytes, 2);

        Assert.Equal(0.5, waveform[0], 2);
        Assert.Equal(1, waveform[1], 2);
    }
}
