namespace Blackbox.Dock;

public enum ObsDockCommand
{
    StartRecording,
    StopRecording,
    ToggleAutomaticCapture,
    ProtectRecent,
    AddMarker,
    OpenTimeline,
    OpenMicrophone
}

public sealed record ObsDockState(
    bool IsRecording,
    double ElapsedSeconds,
    bool IsAutomaticCaptureEnabled,
    bool IsObsReady,
    bool IsBusy,
    string CurrentGame,
    string Status,
    bool CanStart,
    bool CanStop,
    bool CanToggleAutomaticCapture,
    bool CanProtectRecent,
    bool CanAddMarker);

public interface IObsDockControlTarget
{
    Task<ObsDockState> GetStateAsync(CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        ObsDockCommand command,
        CancellationToken cancellationToken = default);
}

public sealed record ObsDockEndpoint(Uri Url, int Port);
