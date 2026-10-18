using System.Text.Json;
using Blackbox.App;

namespace Blackbox.Capture.Tests;

public sealed class RecordingSetupProtocolTests
{
    [Theory]
    [InlineData(42, true, true, 42, true)]
    [InlineData(42, false, true, 42, false)]
    [InlineData(42, true, false, 42, false)]
    [InlineData(41, true, true, 42, false)]
    [InlineData(0, true, true, 0, false)]
    public void Readiness_requires_the_current_helper_and_started_scene(int owner, bool started, bool ready, int expectedOwner, bool expected)
        => Assert.Equal(expected, new RecordingSetupSceneState(owner, started, ready).IsReadyFor(expectedOwner));

    [Fact]
    public void Old_status_defaults_to_manual_recording()
    {
        var status = JsonSerializer.Deserialize<RecordingSetupStatus>("{\"State\":\"READY\",\"CanRecord\":true}")!;
        Assert.False(status.AutoRecord);
        var automatic = status with { AutoRecord = true };
        Assert.True(JsonSerializer.Deserialize<RecordingSetupStatus>(JsonSerializer.Serialize(automatic))!.AutoRecord);
    }
}
