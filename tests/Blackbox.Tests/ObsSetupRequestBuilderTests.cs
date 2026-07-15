using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;

namespace Blackbox.Tests;

public sealed class ObsSetupRequestBuilderTests
{
    [Fact]
    public void BuildSetupRequests_maps_plan_to_obs_websocket_requests()
    {
        var plan = new ObsSetupPlanner().CreateDefaultPlan(new RecordingSettings { RecordingLocation = "C:\\Recordings" });
        var builder = new ObsSetupRequestBuilder();

        var requests = builder.BuildSetupRequests(plan);

        Assert.Contains(requests, static request => request.RequestType == "CreateProfile");
        Assert.Contains(requests, static request => request.RequestType == "CreateSceneCollection");
        Assert.Contains(requests, static request => request.RequestType == "SetRecordDirectory");
        Assert.Equal(5, requests.Count(static request => request.RequestType == "CreateInput"));
        Assert.Equal(5, requests.Count(static request => request.RequestType == "SetInputAudioTracks"));
        Assert.Equal(4, requests.Count(static request => request.RequestType == "CreateSourceFilter"));
        Assert.Equal("SetCurrentProgramScene", requests[^1].RequestType);
    }
}
