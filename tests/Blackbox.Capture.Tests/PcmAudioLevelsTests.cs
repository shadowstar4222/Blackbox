using NAudio.Wave;

namespace Blackbox.Capture.Tests;

public sealed class PcmAudioLevelsTests
{
    [Fact]
    public void Float_peak_and_rms_are_independent()
    {
        var bytes = new[] { 1f, 0f, -1f, 0f }.SelectMany(BitConverter.GetBytes).ToArray();
        var result = PcmAudioLevels.Measure(bytes, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.Equal(0, result.PeakDb, 6);
        Assert.Equal(-3.0103, result.RmsDb, 4);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Integer_samples_support_negative_full_scale(int bits)
    {
        var bytes = new byte[bits / 8];
        if (bits != 8) bytes[^1] = 128;
        var result = PcmAudioLevels.Measure(bytes, new WaveFormat(48000, bits, 1));
        Assert.Equal(0, result.PeakDb);
        Assert.Equal(0, result.RmsDb);
    }

    [Fact]
    public void Silence_and_invalid_float_samples_remain_finite()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        Assert.Equal((-100d, -100d), PcmAudioLevels.Measure([], format));
        var bytes = new[] { float.NaN, float.PositiveInfinity, 0f }.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal((-100d, -100d), PcmAudioLevels.Measure(bytes, format));
    }
}
