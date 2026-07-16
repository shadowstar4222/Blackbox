using System.Text.Json.Nodes;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class ObsWebSocketController(
    IObsWebSocketRpcClient rpcClient,
    IObsConnectionSettingsProvider connectionSettingsProvider,
    ObsSetupRequestBuilder requestBuilder,
    ILogger<ObsWebSocketController> logger) : IObsController
{
    public Task LaunchAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("OBS launch requested; one-click setup owns the portable OBS process.");
        return Task.CompletedTask;
    }

    public Task<ObsConnectionStatus> TestConnectionAsync(
        ObsConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        return rpcClient.TestConnectionAsync(settings, cancellationToken);
    }

    public async Task ApplySetupPlanAsync(
        ObsConnectionSettings settings,
        ObsSetupPlan plan,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        plan.Validate();
        Directory.CreateDirectory(plan.RecordingDirectory);

        await EnsureProfileAsync(settings, plan.ProfileName, cancellationToken);
        await rpcClient.SendBatchAsync(
            settings,
            requestBuilder.BuildRecordingConfigurationRequests(plan.RecordingDirectory, plan.SegmentMinutes),
            cancellationToken);
        await ReloadProfileAsync(settings, plan.ProfileName, cancellationToken);
        await EnsureSceneCollectionAsync(settings, plan.SceneCollectionName, cancellationToken);
        await EnsureSceneAsync(settings, plan.SceneName, cancellationToken);
        await ValidateKindsAsync(settings, plan, cancellationToken);
        await EnsureInputsAsync(settings, plan, cancellationToken);
        var (videoSceneItemId, audioSceneItemId) = await GetGameSceneItemIdsAsync(settings, cancellationToken);
        await SendBatchWithoutResultAsync(
            settings,
            requestBuilder.BuildGameCaptureDeactivationRequests(videoSceneItemId, audioSceneItemId),
            cancellationToken);
        await EnsureFiltersAsync(settings, plan, cancellationToken);
        await rpcClient.SendRequestAsync(
            settings,
            new ObsRequest("SetCurrentProgramScene", new JsonObject { ["sceneName"] = plan.SceneName }),
            cancellationToken);

        logger.LogInformation(
            "OBS setup verified. Profile={ProfileName}, Collection={SceneCollectionName}, Scene={SceneName}.",
            plan.ProfileName,
            plan.SceneCollectionName,
            plan.SceneName);
    }

    public Task ConfigureSegmentedRecordingAsync(
        string recordingDirectory,
        int segmentMinutes,
        CancellationToken cancellationToken = default)
    {
        var requests = requestBuilder.BuildRecordingConfigurationRequests(recordingDirectory, segmentMinutes);
        return SendBatchWithoutResultAsync(connectionSettingsProvider.Current, requests, cancellationToken);
    }

    public Task ConfigureAudioAsync(
        AudioRoutingProfile profile,
        MicrophoneProcessingSettings microphoneSettings,
        CancellationToken cancellationToken = default)
    {
        profile.Validate();
        microphoneSettings.Validate();
        var requests = requestBuilder.BuildAudioRequests(profile, microphoneSettings);
        return SendBatchWithoutResultAsync(connectionSettingsProvider.Current, requests, cancellationToken);
    }

    public async Task ConfigureGameCaptureAsync(
        GameCaptureTarget target,
        CancellationToken cancellationToken = default)
    {
        target.Validate();
        var settings = connectionSettingsProvider.Current;
        await rpcClient.SendRequestAsync(
            settings,
            new ObsRequest("SetCurrentProgramScene", new JsonObject { ["sceneName"] = "Blackbox Recording" }),
            cancellationToken);
        var (videoSceneItemId, audioSceneItemId) = await GetGameSceneItemIdsAsync(settings, cancellationToken);

        await SendBatchWithoutResultAsync(
            settings,
            requestBuilder.BuildGameCaptureDeactivationRequests(videoSceneItemId, audioSceneItemId),
            cancellationToken);
        await SendBatchWithoutResultAsync(
            settings,
            requestBuilder.BuildGameCaptureRequests(target),
            cancellationToken);
        await SendBatchWithoutResultAsync(
            settings,
            requestBuilder.BuildGameCaptureActivationRequests(target, videoSceneItemId, audioSceneItemId),
            cancellationToken);
        logger.LogInformation(
            "OBS scene bound to {GameTitle} ({GameExecutable}) at {Width}x{Height}. DetectionSources={DetectionSources}.",
            target.Title,
            target.ExecutableName,
            target.WindowWidth,
            target.WindowHeight,
            target.DetectionSources);
    }

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        await rpcClient.SendRequestAsync(
            connectionSettingsProvider.Current,
            new ObsRequest("StartRecord"),
            cancellationToken);
        logger.LogInformation("OBS confirmed recording start.");
    }

    public async Task<string?> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        var response = await rpcClient.SendRequestAsync(
            connectionSettingsProvider.Current,
            new ObsRequest("StopRecord"),
            cancellationToken);
        var outputPath = response.ResponseData?["outputPath"]?.GetValue<string>();
        logger.LogInformation("OBS confirmed recording stop. OutputPath={OutputPath}", outputPath);
        return outputPath;
    }

    private async Task EnsureProfileAsync(
        ObsConnectionSettings settings,
        string profileName,
        CancellationToken cancellationToken)
    {
        var response = await rpcClient.SendRequestAsync(settings, new ObsRequest("GetProfileList"), cancellationToken);
        var profiles = GetStringSet(response, "profiles");
        var current = response.ResponseData?["currentProfileName"]?.GetValue<string>();
        if (!profiles.Contains(profileName))
        {
            await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("CreateProfile", new JsonObject { ["profileName"] = profileName }),
                cancellationToken);
        }
        else if (!string.Equals(current, profileName, StringComparison.Ordinal))
        {
            await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("SetCurrentProfile", new JsonObject { ["profileName"] = profileName }),
                cancellationToken);
        }
    }

    private async Task EnsureSceneCollectionAsync(
        ObsConnectionSettings settings,
        string collectionName,
        CancellationToken cancellationToken)
    {
        var response = await rpcClient.SendRequestAsync(settings, new ObsRequest("GetSceneCollectionList"), cancellationToken);
        var collections = GetStringSet(response, "sceneCollections");
        var current = response.ResponseData?["currentSceneCollectionName"]?.GetValue<string>();
        if (!collections.Contains(collectionName))
        {
            await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("CreateSceneCollection", new JsonObject { ["sceneCollectionName"] = collectionName }),
                cancellationToken);
        }
        else if (!string.Equals(current, collectionName, StringComparison.Ordinal))
        {
            await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("SetCurrentSceneCollection", new JsonObject { ["sceneCollectionName"] = collectionName }),
                cancellationToken);
        }
    }

    private async Task ReloadProfileAsync(
        ObsConnectionSettings settings,
        string profileName,
        CancellationToken cancellationToken)
    {
        var response = await rpcClient.SendRequestAsync(settings, new ObsRequest("GetProfileList"), cancellationToken);
        var reloadProfile = GetStringSet(response, "profiles")
            .FirstOrDefault(candidate => !candidate.Equals(profileName, StringComparison.Ordinal));
        var removeTemporaryProfile = reloadProfile is null;
        reloadProfile ??= $"Blackbox Setup {Guid.NewGuid():N}";
        if (removeTemporaryProfile)
        {
            await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("CreateProfile", new JsonObject { ["profileName"] = reloadProfile }),
                cancellationToken);
        }

        await rpcClient.SendRequestAsync(
            settings,
            new ObsRequest("SetCurrentProfile", new JsonObject { ["profileName"] = reloadProfile }),
            cancellationToken);
        await rpcClient.SendRequestAsync(
            settings,
            new ObsRequest("SetCurrentProfile", new JsonObject { ["profileName"] = profileName }),
            cancellationToken);

        if (removeTemporaryProfile)
        {
            await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("RemoveProfile", new JsonObject { ["profileName"] = reloadProfile }),
                cancellationToken);
        }
    }

    private async Task EnsureSceneAsync(
        ObsConnectionSettings settings,
        string sceneName,
        CancellationToken cancellationToken)
    {
        var response = await rpcClient.SendRequestAsync(settings, new ObsRequest("GetSceneList"), cancellationToken);
        var sceneNames = GetObjectNameSet(response, "scenes", "sceneName");
        if (!sceneNames.Contains(sceneName))
        {
            await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("CreateScene", new JsonObject { ["sceneName"] = sceneName }),
                cancellationToken);
        }
    }

    private async Task ValidateKindsAsync(
        ObsConnectionSettings settings,
        ObsSetupPlan plan,
        CancellationToken cancellationToken)
    {
        var responses = await rpcClient.SendBatchAsync(
            settings,
            [
                new ObsRequest("GetInputKindList", new JsonObject { ["unversioned"] = true }),
                new ObsRequest("GetSourceFilterKindList")
            ],
            cancellationToken);
        var inputKinds = GetStringSet(responses[0], "inputKinds");
        var filterKinds = GetStringSet(responses[1], "sourceFilterKinds");
        var missingInputs = plan.Sources.Select(static source => source.Kind).Distinct().Where(kind => !inputKinds.Contains(kind));
        var missingFilters = plan.Filters.Select(static filter => filter.Kind).Distinct().Where(kind => !filterKinds.Contains(kind));
        var missing = missingInputs.Concat(missingFilters).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"This OBS build is missing required Blackbox capture components: {string.Join(", ", missing)}.");
        }
    }

    private async Task EnsureInputsAsync(
        ObsConnectionSettings settings,
        ObsSetupPlan plan,
        CancellationToken cancellationToken)
    {
        var response = await rpcClient.SendRequestAsync(settings, new ObsRequest("GetInputList"), cancellationToken);
        var existingInputs = GetObjectNameSet(response, "inputs", "inputName");
        var requests = new List<ObsRequest>();
        var audioRequests = requestBuilder.BuildAudioRequests(
                plan.AudioRoutingProfile,
                plan.MicrophoneProcessingSettings)
            .ToDictionary(
                static request => request.RequestData?["inputName"]?.GetValue<string>() ?? string.Empty,
                StringComparer.Ordinal);
        foreach (var source in plan.Sources)
        {
            if (!existingInputs.Contains(source.Name))
            {
                requests.Add(new ObsRequest("CreateInput", new JsonObject
                {
                    ["sceneName"] = plan.SceneName,
                    ["inputName"] = source.Name,
                    ["inputKind"] = source.Kind,
                    ["inputSettings"] = ToJsonObject(source.Settings),
                    ["sceneItemEnabled"] = true
                }));
            }

            if (audioRequests.TryGetValue(source.Name, out var audioRequest))
            {
                requests.Add(audioRequest);
            }
        }

        await rpcClient.SendBatchAsync(settings, requests, cancellationToken);
    }

    private async Task EnsureFiltersAsync(
        ObsConnectionSettings settings,
        ObsSetupPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var sourceGroup in plan.Filters.GroupBy(static filter => filter.SourceName))
        {
            var response = await rpcClient.SendRequestAsync(
                settings,
                new ObsRequest("GetSourceFilterList", new JsonObject { ["sourceName"] = sourceGroup.Key }),
                cancellationToken);
            var existingFilters = GetObjectNameSet(response, "filters", "filterName");
            var requests = sourceGroup
                .Where(filter => !existingFilters.Contains(filter.Name))
                .Select(filter => new ObsRequest("CreateSourceFilter", new JsonObject
                {
                    ["sourceName"] = filter.SourceName,
                    ["filterName"] = filter.Name,
                    ["filterKind"] = filter.Kind,
                    ["filterSettings"] = ToJsonObject(filter.Settings)
                }))
                .ToArray();
            await rpcClient.SendBatchAsync(settings, requests, cancellationToken);
        }
    }

    private async Task SendBatchWithoutResultAsync(
        ObsConnectionSettings settings,
        IReadOnlyList<ObsRequest> requests,
        CancellationToken cancellationToken)
    {
        await rpcClient.SendBatchAsync(settings, requests, cancellationToken);
    }

    private static ObsRequest SceneItemIdRequest(string sourceName) =>
        new("GetSceneItemId", new JsonObject
        {
            ["sceneName"] = "Blackbox Recording",
            ["sourceName"] = sourceName
        });

    private async Task<(int VideoSceneItemId, int AudioSceneItemId)> GetGameSceneItemIdsAsync(
        ObsConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var responses = await rpcClient.SendBatchAsync(
            settings,
            [
                SceneItemIdRequest("Blackbox Game Capture"),
                SceneItemIdRequest("Blackbox Game Audio")
            ],
            cancellationToken);
        return (
            GetSceneItemId(responses[0], "Blackbox Game Capture"),
            GetSceneItemId(responses[1], "Blackbox Game Audio"));
    }

    private static int GetSceneItemId(ObsResponse response, string sourceName) =>
        response.ResponseData?["sceneItemId"]?.GetValue<int>()
        ?? throw new InvalidOperationException($"OBS scene is missing {sourceName}. Run Setup OBS again.");

    private static HashSet<string> GetStringSet(ObsResponse response, string fieldName) =>
        response.ResponseData?[fieldName]?.AsArray()
            .Select(static item => item?.GetValue<string>() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    private static HashSet<string> GetObjectNameSet(ObsResponse response, string fieldName, string nameField) =>
        response.ResponseData?[fieldName]?.AsArray()
            .Select(item => item?[nameField]?.GetValue<string>() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> settings)
    {
        var result = new JsonObject();
        foreach (var (key, value) in settings)
        {
            if (bool.TryParse(value, out var booleanValue))
            {
                result[key] = booleanValue;
            }
            else if (double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var numberValue))
            {
                result[key] = numberValue;
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }
}
