using Blackbox.Recording;

namespace Blackbox.Tests;

public sealed class MicrophoneLevelMeterTests
{
    [Fact]
    public void CreateSnapshot_reports_peak_and_rms_in_dbfs()
    {
        var now = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var meter = new MicrophoneLevelMeter(new FixedClock(now));

        var snapshot = meter.CreateSnapshot("Mic", [0.5f, -0.25f, 0.25f, 0f]);

        Assert.Equal("Mic", snapshot.SourceName);
        Assert.Equal(now, snapshot.CapturedAt);
        Assert.InRange(snapshot.PeakDb, -6.1, -5.9);
        Assert.InRange(snapshot.RmsDb, -10.4, -10.1);
    }
}
