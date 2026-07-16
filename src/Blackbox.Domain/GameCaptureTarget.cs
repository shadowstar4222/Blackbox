namespace Blackbox.Domain;

public sealed record GameCaptureTarget(
    int ProcessId,
    string ExecutablePath,
    string ExecutableName,
    string Title,
    string ObsWindowIdentifier,
    GameDetectionSource DetectionSources)
{
    public string Identity => Path.GetFullPath(ExecutablePath).ToUpperInvariant();

    public void Validate()
    {
        if (ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(ExecutablePath) ||
            string.IsNullOrWhiteSpace(ExecutableName) ||
            string.IsNullOrWhiteSpace(Title) ||
            string.IsNullOrWhiteSpace(ObsWindowIdentifier))
        {
            throw new InvalidOperationException("A detected game requires a process, executable, title, and capture window.");
        }

        if (!DetectionSources.HasFlag(GameDetectionSource.ForegroundWindow))
        {
            throw new InvalidOperationException("Automatic game capture currently requires a foreground window.");
        }
    }
}
