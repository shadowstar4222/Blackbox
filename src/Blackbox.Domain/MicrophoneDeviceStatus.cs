namespace Blackbox.Domain;

public sealed record MicrophoneDeviceStatus(
    string DeviceId,
    MicrophoneConnectionState State,
    DateTimeOffset ObservedAt);
