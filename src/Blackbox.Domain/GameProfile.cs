namespace Blackbox.Domain;

public sealed record GameProfile(
    string ExecutablePath,
    string DisplayName,
    bool AutomaticRecordingEnabled,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt)
{
    public string Identity => Path.GetFullPath(ExecutablePath).ToUpperInvariant();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath) ||
            string.IsNullOrWhiteSpace(DisplayName) ||
            !Path.IsPathFullyQualified(ExecutablePath))
        {
            throw new InvalidOperationException("A game profile requires a full executable path and display name.");
        }
    }
}
