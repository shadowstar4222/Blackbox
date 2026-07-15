using System.Text.Json.Nodes;

namespace Blackbox.Infrastructure;

public static class ObsProtocolParser
{
    public static ObsResponse ParseResponse(JsonObject response)
    {
        var status = response["requestStatus"]?.AsObject()
            ?? throw new InvalidOperationException("OBS response did not contain request status data.");
        return new ObsResponse(
            response["requestType"]?.GetValue<string>() ?? "UnknownRequest",
            status["result"]?.GetValue<bool>() ?? false,
            status["code"]?.GetValue<int>() ?? 0,
            status["comment"]?.GetValue<string>(),
            response["responseData"]?.DeepClone().AsObject());
    }

    public static void EnsureSuccessful(IReadOnlyList<ObsResponse> responses)
    {
        var failures = responses.Where(static response => !response.IsSuccessful).ToArray();
        if (failures.Length > 0)
        {
            throw new ObsRequestFailedException(failures);
        }
    }
}
