using Blackbox.App;
using NAudio.Wave;

namespace Blackbox.Capture.Tests;

public sealed class RecordingSetupAudioTests
{
    [Fact]
    public void Loop_crosses_eof_without_silence_or_short_reads()
    {
        using var input = new RawSourceWaveStream(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), new WaveFormat(48000, 16, 2));
        var loop = new LoopingSetupWave(input);
        var buffer = new byte[28];
        Assert.Equal(28, loop.Read(buffer, 0, buffer.Length));
        Assert.Equal(Enumerable.Range(0, 28).Select(i => (byte)(i % 8 + 1)), buffer);
        Assert.Equal(28, loop.Read(buffer, 0, buffer.Length));
        Assert.Equal(Enumerable.Range(0, 28).Select(i => (byte)((i + 4) % 8 + 1)), buffer);
    }

    [Fact]
    public void Empty_wave_terminates_instead_of_spinning()
    {
        using var input = new RawSourceWaveStream(new MemoryStream(), new WaveFormat(48000, 16, 2));
        Assert.Equal(0, new LoopingSetupWave(input).Read(new byte[16], 0, 16));
    }
}
