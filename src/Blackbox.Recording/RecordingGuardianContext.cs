using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Recording;

public interface IRecordingGuardianContext
{
    bool IsRecording { get; }
    bool IsRecordingTransitioning { get; }
    bool IsAutomaticCaptureEnabled { get; }
    GameCaptureTarget? Target { get; }
}

public sealed class RecordingGuardianContext(
    RecordingCoordinator recordingCoordinator,
    AutomaticCaptureService automaticCapture,
    IObsController? controller = null) : IRecordingGuardianContext
{
    public bool IsRecording => recordingCoordinator.IsRecording;
    public bool IsRecordingTransitioning => recordingCoordinator.IsTransitioning;
    public bool IsAutomaticCaptureEnabled => automaticCapture.IsEnabled;
    public GameCaptureTarget? Target => controller is INativeRecorderController native
        ? native.Target
        : automaticCapture.Status.Target;
}
