namespace Blackbox.Domain;

[Flags]
public enum GameDetectionSource
{
    None = 0,
    ForegroundWindow = 1,
    SteamProcessTree = 2,
    SteamLibrary = 4,
    ConfiguredExecutable = 8,
    GpuActivity = 16
}
