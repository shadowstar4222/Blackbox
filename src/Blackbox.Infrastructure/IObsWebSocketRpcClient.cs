namespace Blackbox.Infrastructure;

using Blackbox.Domain;

public interface IObsWebSocketRpcClient
{
    Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default);
    Task<ObsResponse> SendRequestAsync(ObsConnectionSettings settings, ObsRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ObsResponse>> SendBatchAsync(ObsConnectionSettings settings, IReadOnlyList<ObsRequest> requests, CancellationToken cancellationToken = default);
}
