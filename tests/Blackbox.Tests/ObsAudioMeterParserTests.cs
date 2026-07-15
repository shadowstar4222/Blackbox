using System.Text.Json.Nodes;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class ObsAudioMeterParserTests
{
    [Fact]
    public void Parse_reads_peak_and_magnitude_from_the_loudest_channel()
    {
        var capturedAt = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
        var payload = JsonNode.Parse("""
            {
              "op": 5,
              "d": {
                "eventType": "InputVolumeMeters",
                "eventData": {
                  "inputs": [
                    {
                      "inputName": "Blackbox Raw Microphone",
                      "inputLevelsMul": [[0.1, 0.25, 0.3], [0.2, 0.5, 0.6]]
                    }
                  ]
                }
              }
            }
            """)!.AsObject();

        var result = ObsAudioMeterParser.Parse(payload, "Blackbox Raw Microphone", capturedAt);

        Assert.NotNull(result);
        Assert.Equal(-6.02, result.PeakDb, 2);
        Assert.Equal(-13.98, result.RmsDb, 2);
        Assert.Equal(capturedAt, result.CapturedAt);
    }
}
