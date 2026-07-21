namespace Blackbox.Infrastructure;

public sealed record ObsProvisioningOptions
{
    public required string PortableRootDirectory { get; init; }
    public string? ConnectionSettingsPath { get; init; }
    public string? MicrophoneSettingsPath { get; init; }
    public string? AutomaticCaptureSettingsPath { get; init; }
    public string? GameCaptureSelectionSettingsPath { get; init; }
    public bool SearchSystemInstallations { get; init; } = true;
    public IReadOnlyList<string> AdditionalInstallationSearchPaths { get; init; } = [];
    public Uri LatestReleaseApiUri { get; init; } = new("https://api.github.com/repos/obsproject/obs-studio/releases/latest");
}
