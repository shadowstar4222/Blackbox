namespace Blackbox.Domain;

public sealed record AudioRoutingProfile
{
    public required IReadOnlyList<AudioApplicationAssignment> ApplicationAssignments { get; init; }
    public required IReadOnlyList<AudioTrack> Tracks { get; init; }
    public bool DisableDesktopAudioWhenIsolatedSourcesAreActive { get; init; } = true;

    public static AudioRoutingProfile Default => new()
    {
        ApplicationAssignments =
        [
            new AudioApplicationAssignment("Discord.exe", AudioCategory.VoiceChat)
        ],
        Tracks =
        [
            new AudioTrack(1, "Full listening mix", AudioCategory.FullMix),
            new AudioTrack(2, "Game audio", AudioCategory.Game),
            new AudioTrack(3, "Voice chat", AudioCategory.VoiceChat),
            new AudioTrack(4, "Raw microphone", AudioCategory.RawMicrophone),
            new AudioTrack(5, "Processed microphone", AudioCategory.ProcessedMicrophone)
        ]
    };

    public void Validate()
    {
        if (Tracks.Count == 0)
        {
            throw new InvalidOperationException("At least one audio track is required.");
        }

        var duplicateTracks = Tracks.GroupBy(static track => track.TrackNumber).FirstOrDefault(static group => group.Count() > 1);
        if (duplicateTracks is not null)
        {
            throw new InvalidOperationException($"Audio track {duplicateTracks.Key} is assigned more than once.");
        }

        if (Tracks.Any(static track => track.TrackNumber is < 1 or > 6))
        {
            throw new InvalidOperationException("OBS recording track numbers must be between 1 and 6.");
        }

        if (DisableDesktopAudioWhenIsolatedSourcesAreActive && ApplicationAssignments.Count == 0)
        {
            throw new InvalidOperationException("Desktop audio can only be disabled when isolated application assignments exist.");
        }
    }
}
