using System.Text.Json.Nodes;

namespace Blackbox.Infrastructure;

public sealed record ObsRequest(string RequestType, JsonObject? RequestData = null);
