namespace Blackbox.Domain;

public sealed record AutomaticMarkerSettings
{
    public bool Enabled { get; init; }
    public bool DetectAudioSpikes { get; init; }
    public bool DetectSceneChanges { get; init; }
    public bool DetectInputRules { get; init; }
    public double AudioSpikeThresholdDb { get; init; } = -8;
    public double SceneLuminanceDeltaThreshold { get; init; } = 0.3;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ContextBefore { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ContextAfter { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (AudioSpikeThresholdDb is < -60 or > 0 ||
            SceneLuminanceDeltaThreshold is <= 0 or > 1 ||
            Cooldown < TimeSpan.Zero ||
            Cooldown > TimeSpan.FromMinutes(30) ||
            ContextBefore < TimeSpan.Zero ||
            ContextAfter < TimeSpan.Zero ||
            ContextBefore > TimeSpan.FromMinutes(30) ||
            ContextAfter > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("Automatic marker settings are invalid.");
        }

        if (Enabled && !DetectAudioSpikes && !DetectSceneChanges && !DetectInputRules)
        {
            throw new InvalidOperationException(
                "Automatic markers require at least one enabled detector.");
        }
    }
}

public sealed record AutomaticMarkerSignal(
    DateTimeOffset Timestamp,
    string GameTitle,
    double? AudioPeakDb,
    double? MeanLuminance,
    string? InputRule = null);

public sealed record AutomaticMarkerDecision(
    TimelineMarkerOrigin Origin,
    string Label,
    string Reason);
