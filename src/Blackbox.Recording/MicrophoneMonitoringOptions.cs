namespace Blackbox.Recording;

public sealed record MicrophoneMonitoringOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}
