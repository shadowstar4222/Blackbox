using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed record UserExperienceSettings
{
    public bool StartWithWindows { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool WatchRememberedGames { get; init; }
    public bool AutoSetupObsAtStartup { get; init; } = true;
    public bool HasCompletedTutorial { get; init; }
    public RecordingQualitySettings RecordingQuality { get; init; } = new();
}
