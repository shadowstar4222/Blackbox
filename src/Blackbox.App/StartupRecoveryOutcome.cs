using Blackbox.Domain;

namespace Blackbox.App;

public sealed record StartupRecoveryOutcome(
    RecordingRecoveryResult RecordingRecovery,
    int IndexedSessionCount,
    bool RecorderReady,
    bool RecordingAdopted,
    bool AutomaticCaptureResumed)
{
    public string? RecorderFailure { get; init; }

    public string Message
    {
        get
        {
            if (!RecorderReady)
            {
                return $"Recording engine unavailable: {RecorderFailure ?? "check the recorder installation"}. " +
                    "Recovery check completed; recording has not started.";
            }

            if (RecordingAdopted && AutomaticCaptureResumed)
            {
                return "Recovered the active recording and resumed automatic capture.";
            }

            if (RecordingAdopted)
            {
                return "Recovered the active recording.";
            }

            if (RecordingRecovery.DamagedFiles > 0)
            {
                return $"Recovery found {RecordingRecovery.DamagedFiles} damaged recording(s). Open Diagnostics for details.";
            }

            if (RecordingRecovery.RecoveredFiles > 0)
            {
                return $"Recovered {RecordingRecovery.RecoveredFiles} recording(s) and preserved the originals.";
            }

            return AutomaticCaptureResumed
                ? "Recovery check complete; automatic capture resumed."
                : "Recovery check complete; recording engine is ready.";
        }
    }
}
