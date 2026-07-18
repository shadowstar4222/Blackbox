namespace Blackbox.Domain;

public static class RecordingDirectoryLayout
{
    public const string ManualRecordingName = "Manual";
    public const string SetupCheckName = "Blackbox Setup Checks";
    public const string RecoveryBackupName = "Blackbox Recovery Backups";

    public static string GetSessionDirectory(
        string recordingRoot,
        string applicationName,
        DateTimeOffset recordedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingRoot);
        var safeApplicationName = SanitizeApplicationName(applicationName);
        var localDate = recordedAt.ToLocalTime().ToString(
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(
            Path.GetFullPath(recordingRoot),
            safeApplicationName,
            localDate);
    }

    public static string GetApplicationName(string recordingRoot, string recordingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingPath);
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(recordingRoot),
            Path.GetFullPath(recordingPath));
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            ? segments[0]
            : "Recording";
    }

    public static bool IsInternalPath(string recordingRoot, string path)
    {
        var applicationName = GetApplicationName(recordingRoot, path);
        return applicationName.Equals(SetupCheckName, StringComparison.OrdinalIgnoreCase) ||
            applicationName.Equals(RecoveryBackupName, StringComparison.OrdinalIgnoreCase);
    }

    public static string SanitizeApplicationName(string? applicationName)
    {
        var candidate = string.IsNullOrWhiteSpace(applicationName)
            ? "Recording"
            : applicationName.Trim();
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(candidate
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        if (sanitized.Length > 80)
        {
            sanitized = sanitized[..80].TrimEnd('.', ' ');
        }

        if (sanitized.Length == 0 || IsReservedWindowsName(sanitized))
        {
            return "Recording";
        }

        return sanitized;
    }

    private static bool IsReservedWindowsName(string value)
    {
        var name = value.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedDevice(name, "COM") ||
            IsNumberedDevice(name, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == 4 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[3] is >= '1' and <= '9';
}
