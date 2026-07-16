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

    [Fact]
    public void BuildGameCaptureRequests_binds_video_and_isolated_audio_to_the_same_window()
    {
        var target = new GameCaptureTarget(
            42,
            "C:\\Steam\\steamapps\\common\\Example\\Example.exe",
            "Example.exe",
            "Example Game",
            "Example Game:ExampleWindow:Example.exe",
            GameDetectionSource.ForegroundWindow | GameDetectionSource.SteamLibrary);

        var requests = new ObsSetupRequestBuilder().BuildGameCaptureRequests(target);

        Assert.Equal(2, requests.Count);
        Assert.All(requests, static request => Assert.Equal("SetInputSettings", request.RequestType));
        Assert.Equal(
            target.ObsWindowIdentifier,
            requests.Single(request => request.RequestData?["inputName"]?.GetValue<string>() == "Blackbox Game Capture")
                .RequestData?["inputSettings"]?["window"]?.GetValue<string>());
        Assert.Equal(
            target.ObsWindowIdentifier,
            requests.Single(request => request.RequestData?["inputName"]?.GetValue<string>() == "Blackbox Game Audio")
                .RequestData?["inputSettings"]?["window"]?.GetValue<string>());
    }

    private static bool HasProfileValue(ObsRequest request, string name, string value) =>
        request.RequestType == "SetProfileParameter" &&
        request.RequestData?["parameterName"]?.GetValue<string>() == name &&
        request.RequestData?["parameterValue"]?.GetValue<string>() == value;
}
