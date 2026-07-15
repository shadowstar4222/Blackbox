using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsWebSocketControllerTests
{
    [Fact]
    public async Task ApplySetupPlanAsync_sends_built_request_batch()
    {
        var rpc = new RecordingRpcClient();
        var controller = new ObsWebSocketController(rpc, new ObsSetupRequestBuilder(), NullLogger<ObsWebSocketController>.Instance);
        var plan = new ObsSetupPlanner().CreateDefaultPlan(new RecordingSettings { RecordingLocation = "C:\\Recordings" });

        await controller.ApplySetupPlanAsync(new ObsConnectionSettings(), plan);

        Assert.Single(rpc.Batches);
        Assert.Contains(rpc.Batches[0], static request => request.RequestType == "CreateInput");
        Assert.Contains(rpc.Batches[0], static request => request.RequestType == "CreateSourceFilter");
    }

    [Fact]
    public async Task StartRecordingAsync_sends_start_record_request()
    {
        var rpc = new RecordingRpcClient();
        var controller = new ObsWebSocketController(rpc, new ObsSetupRequestBuilder(), NullLogger<ObsWebSocketController>.Instance);

        await controller.StartRecordingAsync();

        Assert.Equal("StartRecord", rpc.SingleRequests.Single().RequestType);
    }

    private sealed class RecordingRpcClient : IObsWebSocketRpcClient
    {
        public List<ObsRequest> SingleRequests { get; } = [];
        public List<IReadOnlyList<ObsRequest>> Batches { get; } = [];

        public Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ObsConnectionStatus.Connected());
        }

        public Task SendRequestAsync(ObsConnectionSettings settings, ObsRequest request, CancellationToken cancellationToken = default)
        {
            SingleRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task SendBatchAsync(ObsConnectionSettings settings, IReadOnlyList<ObsRequest> requests, CancellationToken cancellationToken = default)
        {
            Batches.Add(requests);
            return Task.CompletedTask;
        }
    }
}
