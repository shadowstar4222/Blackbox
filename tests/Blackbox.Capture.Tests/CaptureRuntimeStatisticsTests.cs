using System.Text.Json;
using Blackbox.Capture.Protocol;

namespace Blackbox.Capture.Tests;

public sealed class CaptureRuntimeStatisticsTests
{
    [Fact]
    public void Default_commands_and_events_do_not_add_runtime_statistics_to_the_wire()
    {
        Assert.DoesNotContain("IncludeRuntimeStatistics", JsonSerializer.Serialize(new CaptureCommand(), CaptureJson.Options));
        Assert.DoesNotContain("RuntimeStatistics", JsonSerializer.Serialize(new CaptureReply(), CaptureJson.Options));
    }

    [Fact]
    public void Explicit_diagnostic_request_and_statistics_round_trip()
    {
        var command = new CaptureCommand { Id = 1, IncludeRuntimeStatistics = true };
        Assert.True(JsonSerializer.Deserialize<CaptureCommand>(JsonSerializer.Serialize(command, CaptureJson.Options), CaptureJson.Options)!.IncludeRuntimeStatistics);
        var statistics = new CaptureRuntimeStatistics(100, 200, 300, 10, 400, 5, 2, 1);
        var reply = new CaptureReply { RuntimeStatistics = statistics };
        Assert.Equal(statistics, JsonSerializer.Deserialize<CaptureReply>(JsonSerializer.Serialize(reply, CaptureJson.Options), CaptureJson.Options)!.RuntimeStatistics);
    }
}
