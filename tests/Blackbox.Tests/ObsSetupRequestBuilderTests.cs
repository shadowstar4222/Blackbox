using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;

namespace Blackbox.Tests;

public sealed class ObsSetupRequestBuilderTests
{
    [Fact]
    public void BuildAudioRequests_routes_raw_and_processed_microphones_without_duplicate_full_mix()
    {
        var builder = new ObsSetupRequestBuilder();

        var requests = builder.BuildAudioRequests(AudioRoutingProfile.Default, new MicrophoneProcessingSettings());

        Assert.Equal(4, requests.Count);
        Assert.DoesNotContain(requests, static request =>
            request.RequestData?["inputName"]?.GetValue<string>() == "Blackbox Full Mix");
        var raw = requests.Single(static request =>
            request.RequestData?["inputName"]?.GetValue<string>() == "Blackbox Raw Microphone");
        var processed = requests.Single(static request =>
            request.RequestData?["inputName"]?.GetValue<string>() == "Blackbox Processed Microphone");
        Assert.False(raw.RequestData?["inputAudioTracks"]?["1"]?.GetValue<bool>());
        Assert.True(raw.RequestData?["inputAudioTracks"]?["4"]?.GetValue<bool>());
        Assert.True(processed.RequestData?["inputAudioTracks"]?["1"]?.GetValue<bool>());
        Assert.True(processed.RequestData?["inputAudioTracks"]?["5"]?.GetValue<bool>());
    }

    [Fact]
    public void BuildRecordingConfigurationRequests_enables_mkv_time_splitting_and_five_tracks()
    {
        var requests = new ObsSetupRequestBuilder().BuildRecordingConfigurationRequests("C:\\Recordings", 2);

        Assert.Contains(requests, request => HasProfileValue(request, "RecFormat2", "mkv"));
        Assert.Contains(requests, request => HasProfileValue(request, "RecTracks", "31"));
        Assert.Contains(requests, request => HasProfileValue(request, "RecSplitFile", "true"));
        Assert.Contains(requests, request => HasProfileValue(request, "RecSplitFileTime", "2"));
        Assert.Contains(requests, request => HasProfileValue(request, "SampleRate", "48000"));
    }

    private static bool HasProfileValue(ObsRequest request, string name, string value) =>
        request.RequestType == "SetProfileParameter" &&
        request.RequestData?["parameterName"]?.GetValue<string>() == name &&
        request.RequestData?["parameterValue"]?.GetValue<string>() == value;
}
