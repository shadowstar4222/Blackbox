using System.Text.Json.Nodes;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class ObsProtocolParserTests
{
    [Fact]
    public void ParseResponse_reads_status_and_response_data()
    {
        var payload = JsonNode.Parse(
            """
            {
              "requestType": "StopRecord",
              "requestStatus": { "result": true, "code": 100 },
              "responseData": { "outputPath": "C:\\Recordings\\probe.mkv" }
            }
            """)!.AsObject();

        var response = ObsProtocolParser.ParseResponse(payload);

        Assert.True(response.IsSuccessful);
        Assert.Equal(100, response.Code);
        Assert.Equal("C:\\Recordings\\probe.mkv", response.ResponseData?["outputPath"]?.GetValue<string>());
    }

    [Fact]
    public void EnsureSuccessful_reports_each_rejected_obs_request()
    {
        var failure = new ObsResponse("CreateInput", false, 605, "Input kind was not found.");

        var exception = Assert.Throws<ObsRequestFailedException>(() =>
            ObsProtocolParser.EnsureSuccessful([failure]));

        Assert.Contains("CreateInput (605)", exception.Message, StringComparison.Ordinal);
        Assert.Single(exception.FailedResponses);
    }
}
