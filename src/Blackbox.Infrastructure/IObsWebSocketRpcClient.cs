namespace Blackbox.Infrastructure;

using Blackbox.Domain;

public interface IObsWebSocketRpcClient
{
    Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default);
    Task SendRequestAsync(ObsConnectionSettings settings, ObsRequest request, CancellationToken cancellationToken = default);
    Task SendBatchAsync(ObsConnectionSettings settings, IReadOnlyList<ObsRequest> requests, CancellationToken cancellationToken = default);
}
