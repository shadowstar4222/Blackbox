using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class RecordingSettingsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Validate_rejects_segment_duration_outside_supported_range(int minutes)
    {
        var settings = new RecordingSettings
        {
            RecordingLocation = "C:\\Recordings",
            SegmentDurationMinutes = minutes
        };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void Validate_accepts_default_two_minute_segments()
    {
        var settings = new RecordingSettings { RecordingLocation = "C:\\Recordings" };

        settings.Validate();
    }
}
