namespace Blackbox.Domain;

public sealed record RunningApplication(
    int ProcessId,
    string ExecutablePath,
    string ExecutableName,
    string Title,
    string ObsWindowIdentifier,
    int WindowWidth,
    int WindowHeight,
    bool IsForeground,
    GameDetectionSource DetectionSources)
{
    public IReadOnlyList<string> AncestorExecutableNames { get; init; } = [];
    public string Identity => Path.GetFullPath(ExecutablePath).ToUpperInvariant();

    public GameCaptureTarget ToCaptureTarget(GameDetectionSource additionalSources = GameDetectionSource.None) => new(
        ProcessId,
        ExecutablePath,
        ExecutableName,
        Title,
        ObsWindowIdentifier,
        DetectionSources | additionalSources,
        WindowWidth,
        WindowHeight);
}
