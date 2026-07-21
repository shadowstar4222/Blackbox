namespace Blackbox.Domain;

public sealed record GameCaptureSelection(
    string ProfileExecutablePath,
    string TargetExecutablePath,
    string DisplayName)
{
    public string ProfileIdentity => Normalize(ProfileExecutablePath);
    public string TargetIdentity => Normalize(TargetExecutablePath);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileExecutablePath) ||
            string.IsNullOrWhiteSpace(TargetExecutablePath) ||
            string.IsNullOrWhiteSpace(DisplayName) ||
            !Path.IsPathFullyQualified(ProfileExecutablePath) ||
            !Path.IsPathFullyQualified(TargetExecutablePath))
        {
            throw new InvalidOperationException(
                "A capture selection requires full profile and target executable paths plus a display name.");
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();
}
