using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class ObsWebSocketRpcClient(ILogger<ObsWebSocketRpcClient> logger) : IObsWebSocketRpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        try
        {
            await using var session = await ObsWebSocketSession.ConnectAsync(settings, cancellationToken);
            return ObsConnectionStatus.Connected($"Connected to OBS websocket at {settings.Host}:{settings.Port}.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "OBS websocket connection failed.");
            return ObsConnectionStatus.Failed(ex.Message);
        }
    }

    public async Task SendRequestAsync(ObsConnectionSettings settings, ObsRequest request, CancellationToken cancellationToken = default)
    {
        await SendBatchAsync(settings, [request], cancellationToken);
    }

    public async Task SendBatchAsync(ObsConnectionSettings settings, IReadOnlyList<ObsRequest> requests, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (requests.Count == 0)
        {
            return;
        }

        await using var session = await ObsWebSocketSession.ConnectAsync(settings, cancellationToken);
        var requestBatch = new JsonArray();
        foreach (var request in requests)
        {
            requestBatch.Add(new JsonObject
            {
                ["requestType"] = request.RequestType,
                ["requestId"] = Guid.NewGuid().ToString("N"),
                ["requestData"] = request.RequestData?.DeepClone()
            });
        }

        var payload = new JsonObject
        {
            ["op"] = 8,
            ["d"] = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString("N"),
                ["haltOnFailure"] = false,
                ["requests"] = requestBatch
            }
        };

        await session.SendAsync(payload, cancellationToken);
        logger.LogInformation("Sent OBS websocket batch containing {RequestCount} request(s).", requests.Count);
    }

    private sealed class ObsWebSocketSession : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();

        private ObsWebSocketSession()
        {
        }

        public static async Task<ObsWebSocketSession> ConnectAsync(ObsConnectionSettings settings, CancellationToken cancellationToken)
        {
            var session = new ObsWebSocketSession();
            var uri = new UriBuilder("ws", settings.Host, settings.Port).Uri;
            await session._socket.ConnectAsync(uri, cancellationToken);
            var hello = await session.ReceiveAsync(cancellationToken);
            var identify = BuildIdentifyPayload(settings, hello);
            await session.SendAsync(identify, cancellationToken);
            var identified = await session.ReceiveAsync(cancellationToken);
            if (identified["op"]?.GetValue<int>() != 2)
            {
                throw new InvalidOperationException("OBS websocket did not confirm identification.");
            }

            return session;
        }

        public async Task SendAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Blackbox disconnecting", CancellationToken.None);
            }

            _socket.Dispose();
        }

        private async Task<JsonObject> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("OBS websocket closed the connection.");
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(stream.ToArray());
            return JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("OBS websocket returned invalid JSON.");
        }

        private static JsonObject BuildIdentifyPayload(ObsConnectionSettings settings, JsonObject hello)
        {
            var identifyData = new JsonObject { ["rpcVersion"] = 1 };
            var authentication = hello["d"]?["authentication"]?.AsObject();
            if (authentication is not null)
            {
                var challenge = authentication["challenge"]?.GetValue<string>() ?? string.Empty;
                var salt = authentication["salt"]?.GetValue<string>() ?? string.Empty;
                identifyData["authentication"] = BuildAuthentication(settings.Password ?? string.Empty, salt, challenge);
            }

            return new JsonObject
            {
                ["op"] = 1,
                ["d"] = identifyData
            };
        }

        private static string BuildAuthentication(string password, string salt, string challenge)
        {
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }
    }
}
