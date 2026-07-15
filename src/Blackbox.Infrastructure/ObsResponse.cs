using System.Text.Json.Nodes;

namespace Blackbox.Infrastructure;

public sealed record ObsResponse(
    string RequestType,
    bool IsSuccessful,
    int Code,
    string? Comment,
    JsonObject? ResponseData = null)
{
    public static ObsResponse Successful(string requestType, JsonObject? responseData = null) =>
        new(requestType, true, 100, null, responseData);
}
