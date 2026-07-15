using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Blackbox.Domain;
using Microsoft.Win32;

namespace Blackbox.Infrastructure;

public sealed partial class ObsInstallationLocator(ObsProvisioningOptions options) : IObsInstallationLocator
{
    public ObsInstallation? FindExistingInstallation()
    {
        foreach (var executablePath in GetCandidateExecutablePaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(executablePath))
            {
                continue;
            }

            var rootDirectory = Directory.GetParent(executablePath)?.Parent?.Parent?.FullName;
            if (rootDirectory is null ||
                !Directory.Exists(Path.Combine(rootDirectory, "data")) ||
                !Directory.Exists(Path.Combine(rootDirectory, "obs-plugins")))
            {
                continue;
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            var version = versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "installed";
            return new ObsInstallation(rootDirectory, executablePath, version);
        }

        return null;
    }

    private IEnumerable<string> GetCandidateExecutablePaths()
    {
        foreach (var path in options.AdditionalInstallationSearchPaths)
        {
            yield return NormalizeExecutablePath(path);
        }

        if (!options.SearchSystemInstallations || !OperatingSystem.IsWindows())
        {
            yield break;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(programFiles, "obs-studio", "bin", "64bit", "obs64.exe");
        yield return Path.Combine(programFilesX86, "obs-studio", "bin", "64bit", "obs64.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "obs-studio",
            "bin",
            "64bit",
            "obs64.exe");

        foreach (var steamLibrary in GetSteamLibraryDirectories(programFilesX86))
        {
            yield return Path.Combine(
                steamLibrary,
                "steamapps",
                "common",
                "OBS Studio",
                "bin",
                "64bit",
                "obs64.exe");
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetSteamLibraryDirectories(string programFilesX86)
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRegistrySteamPath(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        AddRegistrySteamPath(steamRoots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        AddRegistrySteamPath(steamRoots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        steamRoots.Add(Path.Combine(programFilesX86, "Steam"));

        var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            try
            {
                foreach (var line in File.ReadLines(libraryFile))
                {
                    var match = SteamLibraryPathRegex().Match(line);
                    if (match.Success)
                    {
                        libraries.Add(match.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Other candidates can still provide a usable OBS installation.
            }
        }

        return libraries;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistrySteamPath(
        ISet<string> paths,
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
            // Registry discovery is optional; default Steam paths are checked too.
        }
    }

    private static string NormalizeExecutablePath(string path) =>
        path.EndsWith("obs64.exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, "bin", "64bit", "obs64.exe");

    [GeneratedRegex("^\\s*\"path\"\\s+\"(?<path>.+)\"\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
