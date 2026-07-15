using System.Text.Json.Nodes;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class ObsSetupRequestBuilder
{
    public IReadOnlyList<ObsRequest> BuildSetupRequests(ObsSetupPlan plan)
    {
        plan.Validate();
        var requests = new List<ObsRequest>
        {
            new("CreateProfile", new JsonObject { ["profileName"] = plan.ProfileName }),
            new("SetCurrentProfile", new JsonObject { ["profileName"] = plan.ProfileName }),
            new("CreateSceneCollection", new JsonObject { ["sceneCollectionName"] = plan.SceneCollectionName }),
            new("SetCurrentSceneCollection", new JsonObject { ["sceneCollectionName"] = plan.SceneCollectionName }),
            new("SetRecordDirectory", new JsonObject { ["recordDirectory"] = plan.RecordingDirectory }),
            new("CreateScene", new JsonObject { ["sceneName"] = plan.SceneName })
        };

        foreach (var source in plan.Sources)
        {
            requests.Add(new ObsRequest("CreateInput", new JsonObject
            {
                ["sceneName"] = plan.SceneName,
                ["inputName"] = source.Name,
                ["inputKind"] = source.Kind,
                ["inputSettings"] = ToJsonObject(source.Settings),
                ["sceneItemEnabled"] = true
            }));

            if (source.AudioCategory is not null)
            {
                requests.Add(new ObsRequest("SetInputAudioTracks", new JsonObject
                {
                    ["inputName"] = source.Name,
                    ["inputAudioTracks"] = BuildTrackMap(plan.AudioRoutingProfile, source.AudioCategory.Value)
                }));
            }
        }

        foreach (var filter in plan.Filters)
        {
            requests.Add(new ObsRequest("CreateSourceFilter", new JsonObject
            {
                ["sourceName"] = filter.SourceName,
                ["filterName"] = filter.Name,
                ["filterKind"] = filter.Kind,
                ["filterSettings"] = ToJsonObject(filter.Settings)
            }));
        }

        requests.Add(new ObsRequest("SetCurrentProgramScene", new JsonObject { ["sceneName"] = plan.SceneName }));
        return requests;
    }

    public IReadOnlyList<ObsRequest> BuildRecordingConfigurationRequests(string recordingDirectory)
    {
        return [new ObsRequest("SetRecordDirectory", new JsonObject { ["recordDirectory"] = recordingDirectory })];
    }

    public IReadOnlyList<ObsRequest> BuildAudioRequests(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings)
    {
        profile.Validate();
        microphoneSettings.Validate();
        return profile.Tracks
            .Select(track => new ObsRequest("SetInputAudioTracks", new JsonObject
            {
                ["inputName"] = SourceNameFor(track.Category),
                ["inputAudioTracks"] = BuildTrackMap(profile, track.Category)
            }))
            .ToArray();
    }

    private static JsonObject BuildTrackMap(AudioRoutingProfile profile, AudioCategory category)
    {
        var tracks = new JsonObject();
        foreach (var track in profile.Tracks)
        {
            tracks[$"{track.TrackNumber}"] = track.Category == category || track.Category == AudioCategory.FullMix;
        }

        return tracks;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> settings)
    {
        var json = new JsonObject();
        foreach (var (key, value) in settings)
        {
            json[key] = value;
        }

        return json;
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
}
