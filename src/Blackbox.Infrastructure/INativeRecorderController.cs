using Blackbox.Domain;

namespace Blackbox.Infrastructure;

// The existing controller contract keeps library and timeline services independent of the capture backend.
public interface INativeRecorderController : IObsController
{
    GameCaptureTarget? Target { get; }
    string Description { get; }
    NativeAudioSignal? AudioSignal => null;
    event Action<string>? RecordingFailed;
    event Action? TargetClosed;
    Task ShutdownAsync() => Task.CompletedTask;
}

public readonly record struct NativeAudioSignal(bool Active, bool MixEnabled, double? PeakDb,
    bool MicrophoneEnabled, bool MicrophoneReceiving);
