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
            GameDetectionSource.ForegroundWindow | GameDetectionSource.SteamLibrary,
            2560,
            1440);

        var requests = new ObsSetupRequestBuilder().BuildGameCaptureRequests(target);

        Assert.Equal(4, requests.Count);
        var videoSettings = requests.Single(static request => request.RequestType == "SetVideoSettings");
        Assert.Equal(2560, videoSettings.RequestData?["baseWidth"]?.GetValue<int>());
        Assert.Equal(1440, videoSettings.RequestData?["baseHeight"]?.GetValue<int>());
        var inputRequests = requests.Where(static request => request.RequestType == "SetInputSettings").ToArray();
        Assert.Equal(2, inputRequests.Length);
        Assert.All(inputRequests, static request => Assert.False(request.RequestData?["overlay"]?.GetValue<bool>()));
        Assert.Equal(
            target.ObsWindowIdentifier,
            inputRequests.Single(request => request.RequestData?["inputName"]?.GetValue<string>() == "Blackbox Game Capture")
                .RequestData?["inputSettings"]?["window"]?.GetValue<string>());
        Assert.Equal(
            target.ObsWindowIdentifier,
            inputRequests.Single(request => request.RequestData?["inputName"]?.GetValue<string>() == "Blackbox Game Audio")
                .RequestData?["inputSettings"]?["window"]?.GetValue<string>());
        Assert.Contains(requests, static request =>
            request.RequestType == "SetInputMute" &&
            request.RequestData?["inputMuted"]?.GetValue<bool>() == false);
    }

    [Fact]
    public void BuildGameCaptureRequests_scales_oversized_windows_to_even_obs_dimensions()
    {
        var target = new GameCaptureTarget(
            42,
            "C:\\Games\\Example.exe",
            "Example.exe",
            "Example Game",
            "Example Game:ExampleWindow:Example.exe",
            GameDetectionSource.ConfiguredExecutable,
            7680,
            2161);

        var request = new ObsSetupRequestBuilder().BuildGameCaptureRequests(target)
            .Single(static candidate => candidate.RequestType == "SetVideoSettings");

        Assert.Equal(4096, request.RequestData?["baseWidth"]?.GetValue<int>());
        Assert.Equal(1152, request.RequestData?["baseHeight"]?.GetValue<int>());
    }

    private static bool HasProfileValue(ObsRequest request, string name, string value) =>
        request.RequestType == "SetProfileParameter" &&
        request.RequestData?["parameterName"]?.GetValue<string>() == name &&
        request.RequestData?["parameterValue"]?.GetValue<string>() == value;
}
