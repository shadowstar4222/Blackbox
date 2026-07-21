using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Blackbox.Infrastructure;

public sealed partial class SteamProcessExecutablePathResolver(
    ILogger<SteamProcessExecutablePathResolver> logger) : IProcessExecutablePathResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
    private readonly object _sync = new();
    private Dictionary<int, string> _activePaths = [];
    private Dictionary<string, SteamLogSignature> _logSignatures =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    public string? Resolve(int processId, string executableName)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 || string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        lock (_sync)
        {
            RefreshIfNeeded();
            if (!_activePaths.TryGetValue(processId, out var path) ||
                !Path.GetFileName(path).Equals(executableName, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                return null;
            }

            return path;
        }
    }

    internal static IReadOnlyDictionary<int, string> ParseActiveProcessPaths(IEnumerable<string> lines)
    {
        var activePaths = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            var added = ProcessAddedRegex().Match(line);
            if (added.Success &&
                int.TryParse(added.Groups["pid"].Value, out var processId))
            {
                activePaths[processId] = added.Groups["path"].Value;
                continue;
            }

            var removed = ProcessRemovedRegex().Match(line);
            if (removed.Success &&
                int.TryParse(removed.Groups["pid"].Value, out processId))
            {
                activePaths.Remove(processId);
            }
        }

        return activePaths;
    }

    private void RefreshIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _refreshedAt < CacheDuration)
        {
            return;
        }

        var logPaths = GetSteamRoots()
            .Select(static steamRoot => Path.Combine(steamRoot, "logs", "gameprocess_log.txt"))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var observedSignatures = new Dictionary<string, SteamLogSignature>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var logPath in logPaths)
        {
            try
            {
                var file = new FileInfo(logPath);
                observedSignatures[logPath] = new SteamLogSignature(
                    file.Length,
                    file.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not inspect Steam process tracking at {SteamLogPath}.", logPath);
            }
        }
        if (_logSignatures.Count == observedSignatures.Count &&
            observedSignatures.All(entry =>
                _logSignatures.TryGetValue(entry.Key, out var previous) && previous == entry.Value))
        {
            _refreshedAt = now;
            return;
        }

        var paths = new Dictionary<int, string>();
        var successfulSignatures = new Dictionary<string, SteamLogSignature>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var logPath in observedSignatures.Keys)
        {
            try
            {
                foreach (var (processId, executablePath) in ParseActiveProcessPaths(File.ReadLines(logPath)))
                {
                    paths[processId] = executablePath;
                }

                successfulSignatures[logPath] = observedSignatures[logPath];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not read Steam process tracking from {SteamLogPath}.", logPath);
            }
        }

        _activePaths = paths;
        _logSignatures = successfulSignatures;
        _refreshedAt = now;
    }

    private static HashSet<string> GetSteamRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRegistryPath(roots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        AddRegistryPath(roots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        AddRegistryPath(roots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam"));
        return roots;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryPath(
        HashSet<string> paths,
        RegistryKey hive,
        string subKeyName,
        string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKeyName);
            if (key?.GetValue(valueName) is string path && !string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path.Replace('/', Path.DirectorySeparatorChar));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Registry discovery is optional; the default Steam location is still checked.
        }
    }

    [GeneratedRegex(
        "AppID\\s+\\d+\\s+adding PID\\s+(?<pid>\\d+)\\s+as a tracked process\\s+\\\"+(?<path>[A-Za-z]:\\\\.*?\\.exe)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcessAddedRegex();

    [GeneratedRegex(
        "AppID\\s+\\d+\\s+no longer tracking PID\\s+(?<pid>\\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcessRemovedRegex();

    private readonly record struct SteamLogSignature(long Length, DateTime LastWriteTimeUtc);
}
