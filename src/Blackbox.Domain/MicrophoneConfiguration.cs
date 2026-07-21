namespace Blackbox.Domain;

public sealed record MicrophoneConfiguration
{
    public string DeviceId { get; init; } = "default";
    public string DeviceName { get; init; } = "Default";
    public bool AutomaticallySelectDevice { get; init; } = true;
    public IReadOnlyList<string> ExcludedDeviceIds { get; init; } = [];
    public MicrophoneProcessingSettings ProcessingSettings { get; init; } = new();
}
