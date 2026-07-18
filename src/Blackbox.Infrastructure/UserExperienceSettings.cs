namespace Blackbox.Infrastructure;

public sealed record UserExperienceSettings
{
    public bool StartWithWindows { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool WatchRememberedGames { get; init; }
}
