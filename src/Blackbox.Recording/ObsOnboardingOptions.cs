namespace Blackbox.Recording;

public sealed record ObsOnboardingOptions
{
    public int ConnectionAttempts { get; init; } = 60;
    public TimeSpan ConnectionRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ProbeRecordingDuration { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        if (ConnectionAttempts is < 1 or > 600)
        {
            throw new InvalidOperationException("OBS connection attempts must be between 1 and 600.");
        }

        if (ConnectionRetryDelay < TimeSpan.Zero || ConnectionRetryDelay > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("OBS connection retry delay must be between zero and 30 seconds.");
        }

        if (ProbeRecordingDuration < TimeSpan.Zero || ProbeRecordingDuration > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("OBS probe duration must be between zero and one minute.");
        }
    }
}
