using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class GameProfileSettingsResolverTests
{
    [Fact]
    public void Resolve_combines_global_defaults_with_only_the_selected_games_overrides()
    {
        var defaults = CreateDefaults();
        var first = CreateProfile("First") with
        {
            Overrides = new GameProfileOverrides
            {
                Resolution = RecordingResolution.QuadHd1440,
                FramesPerSecond = 120,
                SegmentDurationMinutes = 5,
                MaximumRetentionHours = 168,
                CaptureMicrophone = false,
                AutomaticMarkers = new AutomaticMarkerSettings
                {
                    Enabled = true,
                    DetectAudioSpikes = true
                }
            }
        };
        var second = CreateProfile("Second");

        var firstEffective = GameProfileSettingsResolver.Resolve(first, defaults);
        var secondEffective = GameProfileSettingsResolver.Resolve(second, defaults);

        Assert.Equal(RecordingResolution.QuadHd1440, firstEffective.RecordingQuality.Resolution);
        Assert.Equal(120, firstEffective.RecordingQuality.FramesPerSecond);
        Assert.Equal(5, firstEffective.Recording.SegmentDurationMinutes);
        Assert.Equal(TimeSpan.FromDays(7), firstEffective.Recording.MaximumRetainedDuration);
        Assert.False(firstEffective.CaptureMicrophone);
        Assert.True(firstEffective.AutomaticMarkers.Enabled);
        Assert.True(firstEffective.AutomaticMarkers.DetectAudioSpikes);
        Assert.False(secondEffective.AutomaticMarkers.Enabled);
        Assert.Contains(nameof(GameProfileOverrides.FramesPerSecond), firstEffective.OverriddenFields);
        Assert.Contains(nameof(GameProfileOverrides.AutomaticMarkers), firstEffective.OverriddenFields);

        Assert.Equal(RecordingResolution.FullHd1080, secondEffective.RecordingQuality.Resolution);
        Assert.Equal(60, secondEffective.RecordingQuality.FramesPerSecond);
        Assert.Equal(2, secondEffective.Recording.SegmentDurationMinutes);
        Assert.Equal(TimeSpan.FromHours(24), secondEffective.Recording.MaximumRetainedDuration);
        Assert.True(secondEffective.CaptureMicrophone);
        Assert.Empty(secondEffective.OverriddenFields);
    }

    [Fact]
    public void Resolve_does_not_mutate_global_defaults()
    {
        var defaults = CreateDefaults();
        var profile = CreateProfile("Maximum") with
        {
            Overrides = new GameProfileOverrides
            {
                FramesPerSecond = 120,
                MaximumStorageGigabytes = 500
            }
        };

        _ = GameProfileSettingsResolver.Resolve(profile, defaults);

        Assert.Equal(60, defaults.RecordingQuality.FramesPerSecond);
        Assert.Equal(50, defaults.Recording.MaximumStorageGigabytes);
    }

    private static GameProfileDefaults CreateDefaults() => new(
        new RecordingQualitySettings(),
        new RecordingSettings { RecordingLocation = "C:\\Recordings" },
        new MicrophoneProcessingSettings(),
        CaptureDelay: TimeSpan.FromSeconds(1));

    private static GameProfile CreateProfile(string name) => new(
        $"C:\\Games\\{name}\\{name}.exe",
        name,
        true,
        DateTimeOffset.Parse("2026-07-25T12:00:00Z"),
        DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
}
