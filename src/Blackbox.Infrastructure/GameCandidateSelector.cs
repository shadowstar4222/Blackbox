using Blackbox.Domain;

namespace Blackbox.Infrastructure;

internal static class GameCandidateSelector
{
    private static readonly HashSet<string> IgnoredExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe",
        "Blackbox.App.exe",
        "ChatGPT.exe",
        "chrome.exe",
        "codex-computer-use.exe",
        "Discord.exe",
        "dwm.exe",
        "explorer.exe",
        "msedge.exe",
        "obs64.exe",
        "SearchHost.exe",
        "Spotify.exe",
        "StartMenuExperienceHost.exe",
        "steam.exe",
        "steamservice.exe",
        "steamwebhelper.exe",
        "SystemSettings.exe",
        "voicemeeter.exe",
        "voicemeeter8.exe",
        "voicemeeterpro.exe"
    };

    public static GameCaptureTarget? Select(
        ForegroundProcessSnapshot foreground,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree)
    {
        if (IsIgnoredExecutable(foreground.ExecutableName) ||
            string.IsNullOrWhiteSpace(foreground.WindowTitle) ||
            string.IsNullOrWhiteSpace(foreground.WindowClassName))
        {
            return null;
        }

        var sources = Classify(
            foreground.ProcessId,
            foreground.ExecutablePath,
            isForeground: true,
            processTree);

        if ((sources & (GameDetectionSource.SteamLibrary | GameDetectionSource.SteamProcessTree)) == 0)
        {
            return null;
        }

        var target = new GameCaptureTarget(
            foreground.ProcessId,
            foreground.ExecutablePath,
            foreground.ExecutableName,
            foreground.WindowTitle,
            $"{foreground.WindowTitle}:{foreground.WindowClassName}:{foreground.ExecutableName}",
            sources);
        target.Validate();
        return target;
    }

    public static bool IsIgnoredExecutable(string executableName) =>
        IgnoredExecutables.Contains(executableName);

    public static GameDetectionSource Classify(
        int processId,
        string executablePath,
        bool isForeground,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree)
    {
        var sources = isForeground ? GameDetectionSource.ForegroundWindow : GameDetectionSource.None;
        if (IsInSteamLibrary(executablePath))
        {
            sources |= GameDetectionSource.SteamLibrary;
        }

        if (HasSteamAncestor(processId, processTree))
        {
            sources |= GameDetectionSource.SteamProcessTree;
        }

        return sources;
    }

    private static bool IsInSteamLibrary(string executablePath) =>
        executablePath.Contains(
            $"{Path.DirectorySeparatorChar}steamapps{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ||
        executablePath.Contains("\\steamapps\\common\\", StringComparison.OrdinalIgnoreCase);

    private static bool HasSteamAncestor(
        int processId,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree)
    {
        var visited = new HashSet<int>();
        var currentId = processId;
        for (var depth = 0; depth < 16 && visited.Add(currentId); depth++)
        {
            if (!processTree.TryGetValue(currentId, out var current))
            {
                return false;
            }

            if (current.ExecutableName.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) ||
                current.ExecutableName.Equals("steamservice.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            currentId = current.ParentProcessId;
        }

        return false;
    }
}

internal sealed record ForegroundProcessSnapshot(
    int ProcessId,
    string ExecutablePath,
    string ExecutableName,
    string WindowTitle,
    string WindowClassName);

internal sealed record ProcessTreeEntry(int ProcessId, int ParentProcessId, string ExecutableName);
