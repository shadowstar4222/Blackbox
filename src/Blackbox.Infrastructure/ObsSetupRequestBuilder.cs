using System.Text.Json.Nodes;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class ObsSetupRequestBuilder
{
    public IReadOnlyList<ObsRequest> BuildRecordingConfigurationRequests(string recordingDirectory, int segmentMinutes)
    {
        if (string.IsNullOrWhiteSpace(recordingDirectory))
        {
            throw new InvalidOperationException("Recording directory is required.");
        }

        if (segmentMinutes is < 1 or > 10)
        {
            throw new InvalidOperationException("Segment duration must be between 1 and 10 minutes.");
        }

        return
        [
            new ObsRequest("SetRecordDirectory", new JsonObject { ["recordDirectory"] = recordingDirectory }),
            ProfileParameter("Output", "Mode", "Advanced"),
            ProfileParameter("AdvOut", "RecType", "Standard"),
            ProfileParameter("AdvOut", "RecFilePath", recordingDirectory),
            ProfileParameter("AdvOut", "RecFormat2", "mkv"),
            ProfileParameter("AdvOut", "RecTracks", "31"),
            ProfileParameter("AdvOut", "RecSplitFile", "true"),
            ProfileParameter("AdvOut", "RecSplitFileType", "Time"),
            ProfileParameter("AdvOut", "RecSplitFileTime", segmentMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ProfileParameter("Audio", "SampleRate", "48000")
        ];
    }

    public IReadOnlyList<ObsRequest> BuildAudioRequests(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings)
    {
        profile.Validate();
        microphoneSettings.Validate();
        return profile.Tracks
            .Where(static track => track.Category != AudioCategory.FullMix)
            .Select(track => new ObsRequest("SetInputAudioTracks", new JsonObject
            {
                ["inputName"] = SourceNameFor(track.Category),
                ["inputAudioTracks"] = BuildTrackMap(profile, track.Category)
            }))
            .ToArray();
    }

    public IReadOnlyList<ObsRequest> BuildGameCaptureRequests(GameCaptureTarget target)
    {
        target.Validate();
        return
        [
            InputSettings("Blackbox Game Capture", new JsonObject
            {
                ["capture_mode"] = "window",
                ["window"] = target.ObsWindowIdentifier,
                ["priority"] = 1
            }),
            InputSettings("Blackbox Game Audio", new JsonObject
            {
                ["window"] = target.ObsWindowIdentifier
            })
        ];
    }

    private static JsonObject BuildTrackMap(AudioRoutingProfile profile, AudioCategory category)
    {
        var tracks = new JsonObject();
        foreach (var track in profile.Tracks)
        {
            tracks[$"{track.TrackNumber}"] = track.Category == category ||
                (track.Category == AudioCategory.FullMix && category != AudioCategory.RawMicrophone);
        }

        return tracks;
    }

    private static string SourceNameFor(AudioCategory category)
    {
        return category switch
        {
            AudioCategory.Game => "Blackbox Game Audio",
            AudioCategory.VoiceChat => "Blackbox Voice Chat",
            AudioCategory.RawMicrophone => "Blackbox Raw Microphone",
            AudioCategory.ProcessedMicrophone => "Blackbox Processed Microphone",
            _ => "Blackbox Full Mix"
        };
    }

    private static ObsRequest ProfileParameter(string category, string name, string value) =>
        new("SetProfileParameter", new JsonObject
        {
            ["parameterCategory"] = category,
            ["parameterName"] = name,
            ["parameterValue"] = value
        });

    private static ObsRequest InputSettings(string inputName, JsonObject inputSettings) =>
        new("SetInputSettings", new JsonObject
        {
            ["inputName"] = inputName,
            ["inputSettings"] = inputSettings,
            ["overlay"] = true
        });
}
