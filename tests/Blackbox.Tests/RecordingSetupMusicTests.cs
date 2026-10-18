using Blackbox.Export;

namespace Blackbox.Tests;

public sealed class RecordingSetupMusicTests
{
    [Fact]
    public void Music_is_stereo_pcm_with_headroom_and_click_free_endpoints()
    {
        using var wave = new MemoryStream();
        RecordingSetupMusic.WriteWave(wave);
        var bytes = wave.ToArray();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
        Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));
        Assert.Equal(80 * 48000 * 4 + 44, bytes.Length);
        Assert.Equal(0, BitConverter.ToInt16(bytes, 44));
        Assert.Equal(0, BitConverter.ToInt16(bytes, bytes.Length - 2));
        var peak = 0;
        double energy = 0;
        var stereoDifferences = 0;
        for (var i = 44; i < bytes.Length; i += 4)
        {
            var left = BitConverter.ToInt16(bytes, i);
            var right = BitConverter.ToInt16(bytes, i + 2);
            peak = Math.Max(peak, Math.Max(Math.Abs((int)left), Math.Abs((int)right)));
            energy += (double)left * left + (double)right * right;
            if (Math.Abs(left - right) > 100) stereoDifferences++;
        }
        Assert.InRange(peak, 17000, 20000);
        Assert.InRange(Math.Sqrt(energy / ((bytes.Length - 44) / 2)) / short.MaxValue, .03, .3);
        Assert.True(stereoDifferences > 48000);
    }

    [Fact]
    public void Canceled_generation_does_not_write_partial_wave()
    {
        using var wave = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RecordingSetupMusic.WriteWave(wave, cancellation.Token));
        Assert.Equal(0, wave.Length);
    }
}
