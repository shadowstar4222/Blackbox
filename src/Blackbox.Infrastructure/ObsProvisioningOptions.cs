namespace Blackbox.Infrastructure;

public sealed record ObsProvisioningOptions
{
    public required string PortableRootDirectory { get; init; }
    public string? ConnectionSettingsPath { get; init; }
    public Uri LatestReleaseApiUri { get; init; } = new("https://api.github.com/repos/obsproject/obs-studio/releases/latest");
}
