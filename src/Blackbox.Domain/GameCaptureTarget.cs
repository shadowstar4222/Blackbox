namespace Blackbox.Domain;

public sealed record GameCaptureTarget(
    int ProcessId,
    string ExecutablePath,
    string ExecutableName,
    string Title,
    string ObsWindowIdentifier,
    GameDetectionSource DetectionSources,
    int WindowWidth = 1920,
    int WindowHeight = 1080)
{
    public string Identity => Path.GetFullPath(ExecutablePath).ToUpperInvariant();

    public void Validate()
    {
        if (ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(ExecutablePath) ||
            string.IsNullOrWhiteSpace(ExecutableName) ||
            string.IsNullOrWhiteSpace(Title) ||
            string.IsNullOrWhiteSpace(ObsWindowIdentifier) ||
            WindowWidth <= 0 ||
            WindowHeight <= 0)
        {
            throw new InvalidOperationException("A detected game requires a process, executable, title, and capture window.");
        }

        if (!DetectionSources.HasFlag(GameDetectionSource.ForegroundWindow) &&
            !DetectionSources.HasFlag(GameDetectionSource.ConfiguredExecutable))
        {
            throw new InvalidOperationException("Automatic game capture requires a foreground or remembered executable window.");
        }
    }
}
