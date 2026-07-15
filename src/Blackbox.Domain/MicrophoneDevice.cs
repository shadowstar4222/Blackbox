namespace Blackbox.Domain;

public sealed record MicrophoneDevice(string Id, string Name, bool IsEnabled = true);
