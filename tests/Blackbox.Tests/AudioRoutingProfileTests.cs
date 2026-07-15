using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class AudioRoutingProfileTests
{
    [Fact]
    public void Default_profile_contains_milestone_three_tracks()
    {
        var profile = AudioRoutingProfile.Default;

        profile.Validate();

        Assert.Contains(profile.Tracks, static track => track.Category == AudioCategory.Game);
        Assert.Contains(profile.Tracks, static track => track.Category == AudioCategory.VoiceChat);
        Assert.Contains(profile.Tracks, static track => track.Category == AudioCategory.RawMicrophone);
        Assert.Contains(profile.Tracks, static track => track.Category == AudioCategory.ProcessedMicrophone);
        Assert.True(profile.DisableDesktopAudioWhenIsolatedSourcesAreActive);
    }

    [Fact]
    public void Validate_rejects_duplicate_track_numbers()
    {
        var profile = AudioRoutingProfile.Default with
        {
            Tracks =
            [
                new AudioTrack(1, "One", AudioCategory.FullMix),
                new AudioTrack(1, "Duplicate", AudioCategory.Game)
            ]
        };

        Assert.Throws<InvalidOperationException>(profile.Validate);
    }
}
