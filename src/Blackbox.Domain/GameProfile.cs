namespace Blackbox.Domain;

public sealed record GameProfile(
    string ExecutablePath,
    string DisplayName,
    bool AutomaticRecordingEnabled,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<string> ExecutableAliases { get; init; } = [];
    public bool CaptureGameAudio { get; init; } = true;
    public bool FollowLauncherHandoff { get; init; } = true;
    public bool PreferGpuActivity { get; init; }

    public string Identity => Path.GetFullPath(ExecutablePath).ToUpperInvariant();

    public bool MatchesExecutablePath(string executablePath) =>
        Path.GetFullPath(ExecutablePath).Equals(
            Path.GetFullPath(executablePath),
            StringComparison.OrdinalIgnoreCase) ||
        ExecutableAliases.Any(alias => Path.GetFullPath(alias).Equals(
            Path.GetFullPath(executablePath),
            StringComparison.OrdinalIgnoreCase));

    public bool IsAlias(string executablePath) => ExecutableAliases.Any(alias =>
        Path.GetFullPath(alias).Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase));

    public IReadOnlySet<string> ExecutableNames =>
        ExecutableAliases
            .Append(ExecutablePath)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath) ||
            string.IsNullOrWhiteSpace(DisplayName) ||
            !Path.IsPathFullyQualified(ExecutablePath))
        {
            throw new InvalidOperationException("A game profile requires a full executable path and display name.");
        }

        var primaryPath = Path.GetFullPath(ExecutablePath);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in ExecutableAliases)
        {
            if (string.IsNullOrWhiteSpace(alias) || !Path.IsPathFullyQualified(alias))
            {
                throw new InvalidOperationException("Game executable aliases must use full paths.");
            }

            var fullAlias = Path.GetFullPath(alias);
            if (fullAlias.Equals(primaryPath, StringComparison.OrdinalIgnoreCase) || !aliases.Add(fullAlias))
            {
                throw new InvalidOperationException("Game executable aliases must be unique and different from the primary executable.");
            }
        }
    }
}
