using Blackbox.Domain;

namespace Blackbox.Recording;

public enum AutomaticCaptureState
{
    Disabled,
    Watching,
    Confirming,
    Starting,
    Recording,
    Stopping,
    Faulted
}

public sealed record AutomaticCaptureStatus(
    AutomaticCaptureState State,
    string Message,
    GameCaptureTarget? Target,
    bool IsRecording);
