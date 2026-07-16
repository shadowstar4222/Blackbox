using Blackbox.Domain;

namespace Blackbox.App;

public sealed record StartupRecoveryOutcome(
    RecordingRecoveryResult RecordingRecovery,
    int IndexedSessionCount,
    bool ObsReady,
    bool RecordingAdopted,
    bool AutomaticCaptureResumed)
{
    public string Message
    {
        get
        {
            if (RecordingAdopted && AutomaticCaptureResumed)
            {
                return "Recovered the active OBS recording and resumed automatic capture.";
            }

            if (RecordingAdopted)
            {
                return "Recovered the active OBS recording.";
            }

            if (RecordingRecovery.DamagedFiles > 0)
            {
                return $"Recovery found {RecordingRecovery.DamagedFiles} damaged recording(s). Open Diagnostics for details.";
            }

            if (RecordingRecovery.RecoveredFiles > 0)
            {
                return $"Recovered {RecordingRecovery.RecoveredFiles} recording(s) and preserved the originals.";
            }

            return ObsReady
                ? "Recovery check complete; OBS is ready."
                : "Recovery check complete; OBS setup is required.";
        }
    }
}
