using System.Text.Json;
using Blackbox.Capture.Protocol;
using Blackbox.Capture.Worker;

namespace Blackbox.Capture.Tests;

public sealed class AudioPeakMeterTests
{
    [Fact]
    public void DistinguishesSilenceFromMissingAndExpiresStaleReadings()
    {
        var meter = new AudioPeakMeter();
        Assert.Null(meter.Read(0).PeakDb);
        meter.Add(0, 10);
        Assert.Equal(-96, meter.Read(10).PeakDb);
        Assert.Null(meter.Read(1011).PeakDb);
    }

    [Fact]
    public void HoldsPeakForOneWindowThenAcceptsQuieterAudio()
    {
        var meter = new AudioPeakMeter();
        meter.Add(0.5, 0);
        meter.Add(0.1, 100);
        Assert.Equal(-6.0206, meter.Read(100).PeakDb!.Value, 4);
        meter.Add(0.1, 250);
        Assert.Equal(-20, meter.Read(250).PeakDb!.Value, 4);
        Assert.Equal(3, meter.Read(250).Packets);
    }

    [Fact]
    public void RejectsInvalidValuesAndClampsOverload()
    {
        var meter = new AudioPeakMeter();
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1 }) meter.Add(value, 0);
        Assert.Equal(0, meter.Read(0).Packets);
        meter.Add(2, 10);
        Assert.Equal(0, meter.Read(10).PeakDb);
    }

    [Fact]
    public void ProtocolRemainsCompatibleWithOldReplies()
    {
        var old = JsonSerializer.Deserialize<CaptureReply>("{\"State\":\"recording\"}", CaptureJson.Options)!;
        Assert.Null(old.AudioPeakDb);
        Assert.False(old.AudioCaptureEnabled);
        var reply = new CaptureReply { AudioCaptureEnabled = true, AudioPeakDb = -12, AudioPacketCount = 40 };
        var copy = JsonSerializer.Deserialize<CaptureReply>(JsonSerializer.Serialize(reply, CaptureJson.Options), CaptureJson.Options)!;
        Assert.Equal(reply.AudioPeakDb, copy.AudioPeakDb);
        Assert.Equal(40, copy.AudioPacketCount);
    }

    [Fact]
    public void ConcurrentPacketsAndReadersDoNotLoseCounts()
    {
        var meter = new AudioPeakMeter();
        Parallel.For(0, 10000, _ => { meter.Add(0.25, 100); meter.Read(100); });
        Assert.Equal(10000, meter.Read(100).Packets);
        Assert.Equal(-12.0412, meter.Read(100).PeakDb!.Value, 4);
    }
}
