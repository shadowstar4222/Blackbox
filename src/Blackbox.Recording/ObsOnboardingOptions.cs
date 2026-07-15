namespace Blackbox.Recording;

public sealed record ObsOnboardingOptions
{
    public int ConnectionAttempts { get; init; } = 60;
    public TimeSpan ConnectionRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ProbeRecordingDuration { get; init; } = TimeSpan.FromSeconds(2);
}
