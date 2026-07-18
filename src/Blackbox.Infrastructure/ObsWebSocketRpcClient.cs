using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class ObsWebSocketRpcClient(
    IClock clock,
    ILogger<ObsWebSocketRpcClient> logger) : IObsWebSocketRpcClient, IObsAudioMeterClient
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

    public async Task<ObsResponse> SendRequestAsync(
        ObsConnectionSettings settings,
        ObsRequest request,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        await using var session = await ObsWebSocketSession.ConnectAsync(settings, cancellationToken);
        var requestId = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["op"] = 6,
            ["d"] = BuildRequestData(request, requestId)
        };

        await session.SendAsync(payload, cancellationToken);
        var responsePayload = await session.ReceiveForRequestAsync(7, requestId, cancellationToken);
        var response = ObsProtocolParser.ParseResponse(responsePayload["d"]?.AsObject()
            ?? throw new InvalidOperationException("OBS returned an invalid request response."));
        ObsProtocolParser.EnsureSuccessful([response]);
        return response;
    }

    public async Task<IReadOnlyList<ObsResponse>> SendBatchAsync(
        ObsConnectionSettings settings,
        IReadOnlyList<ObsRequest> requests,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (requests.Count == 0)
        {
            return [];
        }

        await using var session = await ObsWebSocketSession.ConnectAsync(settings, cancellationToken);
        var requestBatch = new JsonArray();
        foreach (var request in requests)
        {
            requestBatch.Add(BuildRequestData(request, Guid.NewGuid().ToString("N")));
        }

        var batchRequestId = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["op"] = 8,
            ["d"] = new JsonObject
            {
                ["requestId"] = batchRequestId,
                ["haltOnFailure"] = false,
                ["executionType"] = 0,
                ["requests"] = requestBatch
            }
        };

        await session.SendAsync(payload, cancellationToken);
        var responsePayload = await session.ReceiveForRequestAsync(9, batchRequestId, cancellationToken);
        var resultNodes = responsePayload["d"]?["results"]?.AsArray()
            ?? throw new InvalidOperationException("OBS returned an invalid batch response.");
        var responses = resultNodes
            .Select(static node => ObsProtocolParser.ParseResponse(node?.AsObject()
                ?? throw new InvalidOperationException("OBS returned an invalid result in a batch response.")))
            .ToArray();

        ObsProtocolParser.EnsureSuccessful(responses);
        logger.LogInformation("OBS accepted a websocket batch containing {RequestCount} request(s).", requests.Count);
        return responses;
    }

    public async Task<IReadOnlyList<AudioLevelSnapshot>> CaptureInputLevelsAsync(
        ObsConnectionSettings settings,
        string inputName,
        TimeSpan duration,
        IProgress<AudioLevelSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(inputName))
        {
            throw new ArgumentException("An OBS input name is required.", nameof(inputName));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Meter capture duration must be greater than zero.");
        }

        const int inputVolumeMeterSubscription = 1 << 16;
        await using var session = await ObsWebSocketSession.ConnectAsync(
            settings,
            cancellationToken,
            inputVolumeMeterSubscription);
        var snapshots = new List<AudioLevelSnapshot>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            var remaining = duration - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveTimeout.CancelAfter(remaining);
            JsonObject payload;
            try
            {
                payload = await session.ReceiveAsync(receiveTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var snapshot = ObsAudioMeterParser.Parse(payload, inputName, clock.UtcNow);
            if (snapshot is null)
            {
                continue;
            }

            snapshots.Add(snapshot);
            progress?.Report(snapshot);
        }

        return snapshots;
    }

    private static JsonObject BuildRequestData(ObsRequest request, string requestId) => new()
    {
        ["requestType"] = request.RequestType,
        ["requestId"] = requestId,
        ["requestData"] = request.RequestData?.DeepClone()
    };

    private sealed class ObsWebSocketSession : IAsyncDisposable
    {
        private const int MaximumMessageBytes = 4 * 1024 * 1024;
        private readonly ClientWebSocket _socket = new();

        private ObsWebSocketSession()
        {
        }

        public static async Task<ObsWebSocketSession> ConnectAsync(
            ObsConnectionSettings settings,
            CancellationToken cancellationToken,
            int? eventSubscriptions = null)
        {
            var session = new ObsWebSocketSession();
            try
            {
                var uri = new UriBuilder("ws", settings.Host, settings.Port).Uri;
                await session._socket.ConnectAsync(uri, cancellationToken);
                var hello = await session.ReceiveAsync(cancellationToken);
                var identify = BuildIdentifyPayload(settings, hello, eventSubscriptions);
                await session.SendAsync(identify, cancellationToken);
                var identified = await session.ReceiveAsync(cancellationToken);
                if (identified["op"]?.GetValue<int>() != 2)
                {
                    throw new InvalidOperationException("OBS websocket did not confirm identification.");
                }

                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
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

        public async Task<JsonObject> ReceiveForRequestAsync(
            int expectedOpCode,
            string requestId,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var payload = await ReceiveAsync(cancellationToken);
                if (payload["op"]?.GetValue<int>() == expectedOpCode &&
                    payload["d"]?["requestId"]?.GetValue<string>() == requestId)
                {
                    return payload;
                }
            }
        }

        public async Task<JsonObject> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException($"OBS websocket closed the connection: {result.CloseStatusDescription ?? "no reason provided"}.");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("OBS websocket returned a non-text message.");
                }

                if (stream.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("OBS websocket returned an oversized message.");
                }

                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(stream.ToArray());
            return JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("OBS websocket returned invalid JSON.");
        }

        private static JsonObject BuildIdentifyPayload(
            ObsConnectionSettings settings,
            JsonObject hello,
            int? eventSubscriptions)
        {
            var identifyData = new JsonObject { ["rpcVersion"] = 1 };
            if (eventSubscriptions is not null)
            {
                identifyData["eventSubscriptions"] = eventSubscriptions.Value;
            }
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
