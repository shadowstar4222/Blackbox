namespace Blackbox.Domain;

public sealed record ObsSetupPlan
{
    public string ProfileName { get; init; } = "Blackbox";
    public string SceneCollectionName { get; init; } = "Blackbox";
    public string SceneName { get; init; } = "Blackbox Recording";
    public required string RecordingDirectory { get; init; }
    public int SegmentMinutes { get; init; } = 2;
    public required AudioRoutingProfile AudioRoutingProfile { get; init; }
    public required MicrophoneProcessingSettings MicrophoneProcessingSettings { get; init; }
    public required IReadOnlyList<ObsSourcePlan> Sources { get; init; }
    public required IReadOnlyList<ObsFilterPlan> Filters { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            throw new InvalidOperationException("OBS profile name is required.");
        }

        if (string.IsNullOrWhiteSpace(SceneCollectionName))
        {
            throw new InvalidOperationException("OBS scene collection name is required.");
        }

        if (string.IsNullOrWhiteSpace(SceneName))
        {
            throw new InvalidOperationException("OBS scene name is required.");
        }

        if (string.IsNullOrWhiteSpace(RecordingDirectory))
        {
            throw new InvalidOperationException("Recording directory is required.");
        }

        if (SegmentMinutes is < 1 or > 10)
        {
            throw new InvalidOperationException("Segment duration must be between 1 and 10 minutes.");
        }

        AudioRoutingProfile.Validate();
        MicrophoneProcessingSettings.Validate();
    }
}
