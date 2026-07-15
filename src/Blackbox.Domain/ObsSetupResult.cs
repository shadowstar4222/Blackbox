namespace Blackbox.Domain;

public sealed record ObsSetupResult(bool IsSuccessful, string Message, string? ProbeRecordingPath = null)
{
    public static ObsSetupResult Successful(string message, string? probeRecordingPath = null) =>
        new(true, message, probeRecordingPath);

    public static ObsSetupResult Failed(string message) => new(false, message);
}
