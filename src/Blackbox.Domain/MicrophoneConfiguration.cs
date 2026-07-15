namespace Blackbox.Domain;

public sealed record MicrophoneConfiguration
{
    public string DeviceId { get; init; } = "default";
    public string DeviceName { get; init; } = "Default";
    public MicrophoneProcessingSettings ProcessingSettings { get; init; } = new();
}
