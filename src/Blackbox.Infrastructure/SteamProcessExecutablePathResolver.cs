using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Blackbox.Infrastructure;

public sealed partial class SteamProcessExecutablePathResolver : IProcessExecutablePathResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RootCacheDuration = TimeSpan.FromMinutes(1);
    internal const int MaximumReadBytes = 1024 * 1024;
    private const int MaximumLineBytes = 64 * 1024;
    private const int MaximumActivePaths = 16_384;
    private readonly ILogger<SteamProcessExecutablePathResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IEnumerable<string>> _getLogPaths;
    private readonly object _sync = new();
    private Dictionary<int, string> _activePaths = [];
    private readonly Dictionary<string, SteamLogState> _logs = new(StringComparer.OrdinalIgnoreCase);
    private string[] _logPaths = [];
    private long? _refreshedAt;
    private long? _rootsRefreshedAt;

    public SteamProcessExecutablePathResolver(ILogger<SteamProcessExecutablePathResolver> logger)
        : this(logger, TimeProvider.System, () => GetSteamRoots()
            .Select(static root => Path.Combine(root, "logs", "gameprocess_log.txt")))
    {
    }

    internal SteamProcessExecutablePathResolver(
        ILogger<SteamProcessExecutablePathResolver> logger,
        TimeProvider timeProvider,
        Func<IEnumerable<string>> getLogPaths)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _getLogPaths = getLogPaths;
    }

    public string? Resolve(int processId, string executableName, bool forceRefresh = false)
    {
        if (processId <= 0)
        {
            return null;
        }

        lock (_sync)
        {
            // A miss (including a legacy forced request) cannot trigger repeated disk scans.
            RefreshIfNeeded();
            return SelectActiveProcessPath(
                _activePaths,
                processId,
                executableName,
                File.Exists);
        }
    }

    internal static string? SelectActiveProcessPath(
        IReadOnlyDictionary<int, string> activePaths,
        int processId,
        string executableName,
        Func<string, bool> fileExists)
    {
        if (!activePaths.TryGetValue(processId, out var path) ||
            (!string.IsNullOrWhiteSpace(executableName) &&
             !Path.GetFileName(path).Equals(executableName, StringComparison.OrdinalIgnoreCase)) ||
            !fileExists(path))
        {
            return null;
        }

        return path;
    }

    internal static IReadOnlyDictionary<int, string> ParseActiveProcessPaths(IEnumerable<string> lines)
    {
        var activePaths = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            ApplyLine(activePaths, line);
        }

        return activePaths;
    }

    private static void ApplyLine(Dictionary<int, string> activePaths, string line)
    {
        if (line.Length > MaximumLineBytes)
        {
            return;
        }

        var added = ProcessAddedRegex().Match(line);
        if (added.Success &&
            int.TryParse(added.Groups["pid"].Value, out var processId) && processId > 0)
        {
            if (activePaths.Count >= MaximumActivePaths && !activePaths.ContainsKey(processId))
            {
                activePaths.Remove(activePaths.Keys.First());
            }

            activePaths[processId] = added.Groups["path"].Value;
            return;
        }

        var removed = ProcessRemovedRegex().Match(line);
        if (removed.Success &&
            int.TryParse(removed.Groups["pid"].Value, out processId))
        {
            activePaths.Remove(processId);
        }
    }

    private void RefreshIfNeeded()
    {
        if (_refreshedAt is { } refreshed && _timeProvider.GetElapsedTime(refreshed) < CacheDuration)
        {
            return;
        }

        _refreshedAt = _timeProvider.GetTimestamp();
        if (_rootsRefreshedAt is null ||
            _timeProvider.GetElapsedTime(_rootsRefreshedAt.Value) >= RootCacheDuration)
        {
            _logPaths = _getLogPaths().Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            _rootsRefreshedAt = _timeProvider.GetTimestamp();
            foreach (var obsolete in _logs.Keys.Except(_logPaths, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                _logs.Remove(obsolete);
            }
        }

        foreach (var logPath in _logPaths)
        {
            try
            {
                var file = new FileInfo(logPath);
                if (!file.Exists)
                {
                    _logs.Remove(logPath);
                    continue;
                }

                var signature = new SteamLogSignature(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
                if (_logs.TryGetValue(logPath, out var state) && state.Signature == signature)
                {
                    continue;
                }

                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                var reset = state is null || length < state.Offset ||
                    signature.CreationTimeUtc != state.Signature.CreationTimeUtc ||
                    length == state.Offset || !AnchorMatches(stream, state);
                if (reset || length - state!.Offset > MaximumReadBytes)
                {
                    // Start at a bounded tail on startup/rotation; never replay an unbounded history.
                    state = new SteamLogState { Offset = Math.Max(0, length - MaximumReadBytes) };
                    state.DiscardPartialLine = state.Offset > 0;
                }

                stream.Position = state!.Offset;
                var bytes = new byte[(int)Math.Min(MaximumReadBytes, length - state.Offset)];
                stream.ReadExactly(bytes);
                ConsumeBytes(state, bytes);
                state.Offset += bytes.Length;
                stream.Position = Math.Max(0, state.Offset - 128);
                state.Anchor = new byte[(int)(state.Offset - stream.Position)];
                stream.ReadExactly(state.Anchor);
                state.Signature = signature;
                _logs[logPath] = state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logs.Remove(logPath);
                _logger.LogDebug(ex, "Could not read Steam process tracking at {SteamLogPath}.", logPath);
            }
        }

        var paths = new Dictionary<int, string>();
        foreach (var state in _logs.Values)
        {
            foreach (var (processId, executablePath) in state.ActivePaths)
            {
                paths[processId] = executablePath;
            }
        }

        _activePaths = paths;
    }

    private static bool AnchorMatches(FileStream stream, SteamLogState state)
    {
        stream.Position = state.Offset - state.Anchor.Length;
        var anchor = new byte[state.Anchor.Length];
        stream.ReadExactly(anchor);
        return anchor.AsSpan().SequenceEqual(state.Anchor);
    }

    private static void ConsumeBytes(SteamLogState state, byte[] bytes)
    {
        var combined = new byte[state.PendingLine.Length + bytes.Length];
        state.PendingLine.CopyTo(combined, 0);
        bytes.CopyTo(combined, state.PendingLine.Length);
        var start = 0;
        for (var index = 0; index < combined.Length; index++)
        {
            if (combined[index] != (byte)'\n')
            {
                continue;
            }

            if (!state.DiscardPartialLine && index - start <= MaximumLineBytes)
            {
                ApplyLine(state.ActivePaths, Encoding.UTF8.GetString(combined, start, index - start));
            }

            state.DiscardPartialLine = false;
            start = index + 1;
        }

        if (combined.Length - start > MaximumLineBytes)
        {
            state.DiscardPartialLine = true;
        }

        state.PendingLine = state.DiscardPartialLine ? [] : combined[start..];
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

    private readonly record struct SteamLogSignature(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);

    private sealed class SteamLogState
    {
        public SteamLogSignature Signature { get; set; }
        public long Offset { get; set; }
        public byte[] Anchor { get; set; } = [];
        public byte[] PendingLine { get; set; } = [];
        public bool DiscardPartialLine { get; set; }
        public Dictionary<int, string> ActivePaths { get; } = [];
    }
}
