using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class WindowsRunningApplicationCatalog : IRunningApplicationCatalog
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(500);
    private readonly IProcessExecutablePathResolver _processPathResolver;
    private readonly ILogger<WindowsRunningApplicationCatalog> _logger;
    private readonly IWindowCatalogNativeAccess _native;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IReadOnlyList<CatalogEntry> _cached = [];
    private long? _refreshedAt;

    public WindowsRunningApplicationCatalog(
        IProcessExecutablePathResolver processPathResolver,
        ILogger<WindowsRunningApplicationCatalog> logger)
        : this(processPathResolver, logger, new NativeWindowCatalogAccess(), TimeProvider.System)
    {
    }

    internal WindowsRunningApplicationCatalog(
        IProcessExecutablePathResolver processPathResolver,
        ILogger<WindowsRunningApplicationCatalog> logger,
        IWindowCatalogNativeAccess native,
        TimeProvider timeProvider)
    {
        _processPathResolver = processPathResolver;
        _logger = logger;
        _native = native;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RunningApplication>> GetRunningApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_native.IsSupported)
        {
            return [];
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Native discovery and optional log I/O must never run on the caller's UI thread.
            return await Task.Run(() => ReadApplications(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<RunningApplication?> GetRunningApplicationAsync(
        long windowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_native.IsSupported || windowHandle == 0)
        {
            return null;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var window = _native.ReadWindow(windowHandle);
                if (window is null)
                {
                    return null;
                }

                var tree = _native.ReadProcessTree(cancellationToken);
                var entry = CreateEntry(window, tree, new Dictionary<int, string?>(), explicitWindow: true);
                cancellationToken.ThrowIfCancellationRequested();
                return entry is null ? null : Revalidate(entry, _native.GetForegroundWindow());
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private IReadOnlyList<RunningApplication> ReadApplications(CancellationToken cancellationToken)
    {
        if (_refreshedAt is null ||
            _timeProvider.GetElapsedTime(_refreshedAt.Value) >= CacheDuration)
        {
            try
            {
                var handles = _native.EnumerateWindows(cancellationToken);
                var tree = _native.ReadProcessTree(cancellationToken);
                var paths = new Dictionary<int, string?>();
                var entries = new List<CatalogEntry>();
                foreach (var handle in handles.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var window = _native.ReadWindow(handle);
                    if (window is not null && CreateEntry(window, tree, paths) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                _cached = entries;
            }
            catch (Win32Exception ex)
            {
                _logger.LogDebug(ex, "Window discovery failed; revalidating the previous snapshot.");
            }

            _refreshedAt = _timeProvider.GetTimestamp();
        }

        var foreground = _native.GetForegroundWindow();
        var applications = new List<RunningApplication>(_cached.Count);
        foreach (var entry in _cached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Revalidate(entry, foreground) is { } application)
            {
                applications.Add(application);
            }
        }

        return TaskbarWindowCatalogRules.Order(applications);
    }

    private CatalogEntry? CreateEntry(
        WindowCatalogSnapshot window,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree,
        Dictionary<int, string?> paths,
        bool explicitWindow = false)
    {
        if (window.ProcessId <= 0 || window.ProcessId == Environment.ProcessId ||
            !window.Taskbar.IsVisible || window.Taskbar.IsCloaked || !window.IsTopLevel ||
            string.IsNullOrWhiteSpace(window.WindowClass))
        {
            return null;
        }

        try
        {
            if (!paths.TryGetValue(window.ProcessId, out var executablePath))
            {
                var nameHint = processTree.GetValueOrDefault(window.ProcessId)?.ExecutableName ?? string.Empty;
                executablePath = _native.ReadExecutablePath(window.ProcessId) ??
                    _processPathResolver.Resolve(window.ProcessId, nameHint);
                paths[window.ProcessId] = executablePath;
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            {
                return null;
            }

            executablePath = Path.GetFullPath(executablePath);
            var executableName = Path.GetFileName(executablePath);
            if (executableName.Equals("Blackbox.App.exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var detectionSources = GameCandidateSelector.Classify(
                window.ProcessId,
                executablePath,
                isForeground: false,
                processTree);
            if (GameCandidateSelector.IsIgnoredExecutable(executableName) ||
                executableName.Equals("HD2Arsenal.exe", StringComparison.OrdinalIgnoreCase))
            {
                detectionSources = GameDetectionSource.None;
            }

            if (!explicitWindow && !TaskbarWindowCatalogRules.IsEligible(window.Taskbar) &&
                !TaskbarWindowCatalogRules.IsLikelyVisibleGameSurface(
                    window.Taskbar,
                    !string.IsNullOrWhiteSpace(window.Title),
                    window.ClientWidth,
                    window.ClientHeight,
                    detectionSources))
            {
                return null;
            }

            return new CatalogEntry(window, new RunningApplication(
                window.ProcessId,
                executablePath,
                executableName,
                window.Title,
                string.Empty,
                window.ClientWidth,
                window.ClientHeight,
                false,
                detectionSources)
            {
                WindowHandle = window.Handle,
                AncestorExecutableNames = GetAncestorExecutableNames(window.ProcessId, processTree)
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            _logger.LogDebug(ex, "Could not inspect window for process {ProcessId}.", window.ProcessId);
            return null;
        }
    }

    private RunningApplication? Revalidate(CatalogEntry entry, long foreground)
    {
        var window = _native.ReadWindow(entry.Window.Handle);
        if (window is null || window.ProcessId != entry.Window.ProcessId ||
            window.WindowClass != entry.Window.WindowClass || !window.IsTopLevel ||
            !window.Taskbar.IsVisible || window.Taskbar.IsCloaked)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(window.Title)
            ? Path.GetFileNameWithoutExtension(entry.Application.ExecutableName)
            : window.Title;
        var (width, height) = TaskbarWindowCatalogRules.ResolveWindowSize(
            window.ClientWidth, window.ClientHeight, window.RestoredWidth, window.RestoredHeight);
        return entry.Application with
        {
            Title = title,
            ObsWindowIdentifier = $"{title}:{window.WindowClass}:{entry.Application.ExecutableName}",
            WindowWidth = width,
            WindowHeight = height,
            IsForeground = window.Handle == foreground,
            DetectionSources = entry.Application.DetectionSources |
                (window.Handle == foreground ? GameDetectionSource.ForegroundWindow : GameDetectionSource.None),
            IsMinimized = window.IsMinimized,
            IsCapturable = !window.IsMinimized && window.ClientWidth > 0 && window.ClientHeight > 0
        };
    }

    private sealed record CatalogEntry(WindowCatalogSnapshot Window, RunningApplication Application);

    private static string? ReadExecutablePath(uint processId)
    {
        var processHandle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var capacity = 32768;
            var path = new StringBuilder(capacity);
            return NativeMethods.QueryFullProcessImageName(processHandle, 0, path, ref capacity)
                ? path.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }

    private static IReadOnlyDictionary<int, ProcessTreeEntry> ReadProcessTree(CancellationToken cancellationToken)
    {
        var entries = new Dictionary<int, ProcessTreeEntry>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return entries;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>()
            };
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return entries;
            }

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries[(int)entry.ProcessId] = new ProcessTreeEntry(
                    (int)entry.ProcessId,
                    (int)entry.ParentProcessId,
                    entry.ExecutableFile);
                entry.Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
            }
            while (entries.Count < 65_536 && NativeMethods.Process32Next(snapshot, ref entry));

            return entries;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    private static IReadOnlyList<string> GetAncestorExecutableNames(
        int processId,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree)
    {
        var ancestors = new List<string>();
        var visited = new HashSet<int> { processId };
        var currentId = processId;
        for (var depth = 0; depth < 16; depth++)
        {
            if (!processTree.TryGetValue(currentId, out var current) ||
                current.ParentProcessId <= 0 ||
                !visited.Add(current.ParentProcessId) ||
                !processTree.TryGetValue(current.ParentProcessId, out var parent))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(parent.ExecutableName))
            {
                ancestors.Add(parent.ExecutableName);
            }

            currentId = parent.ProcessId;
        }

        return ancestors;
    }

    private static string ReadWindowText(IntPtr window)
    {
        var buffer = new StringBuilder(4096);
        _ = NativeMethods.GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private sealed class NativeWindowCatalogAccess : IWindowCatalogNativeAccess
    {
        public bool IsSupported => OperatingSystem.IsWindows();

        public long GetForegroundWindow() => NativeMethods.GetForegroundWindow().ToInt64();

        public IReadOnlyList<long> EnumerateWindows(CancellationToken cancellationToken)
        {
            var handles = new List<long>();
            var completed = NativeMethods.EnumWindows((window, _) =>
            {
                if (cancellationToken.IsCancellationRequested || handles.Count >= 16_384)
                {
                    return false;
                }

                handles.Add(window.ToInt64());
                return true;
            }, IntPtr.Zero);
            cancellationToken.ThrowIfCancellationRequested();
            if (!completed && handles.Count < 16_384)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return handles;
        }

        public WindowCatalogSnapshot? ReadWindow(long handle)
        {
            var window = new IntPtr(handle);
            var thread = NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (thread == 0 || processId == 0 || processId == Environment.ProcessId ||
                !NativeMethods.IsWindowVisible(window))
            {
                return null;
            }

            var snapshot = NativeMethods.ReadTaskbarWindowSnapshot(window);
            if (snapshot.IsCloaked)
            {
                return null;
            }

            var title = ReadWindowText(window);
            var windowClass = ReadWindowClass(window);
            _ = NativeMethods.GetClientRect(window, out var client);
            var minimized = NativeMethods.IsIconic(window);
            var normal = minimized || client.Right <= client.Left || client.Bottom <= client.Top
                ? NativeMethods.ReadNormalWindowBounds(window)
                : default;
            var isTopLevel = NativeMethods.IsTopLevel(window);
            if (NativeMethods.GetWindowThreadProcessId(window, out var finalProcessId) != thread ||
                finalProcessId != processId)
            {
                return null;
            }

            return new WindowCatalogSnapshot(
                handle, (int)processId, title, windowClass, snapshot,
                client.Right - client.Left, client.Bottom - client.Top,
                normal.Right - normal.Left, normal.Bottom - normal.Top, minimized, isTopLevel);
        }

        public string? ReadExecutablePath(int processId) =>
            WindowsRunningApplicationCatalog.ReadExecutablePath((uint)processId);

        public IReadOnlyDictionary<int, ProcessTreeEntry> ReadProcessTree(CancellationToken cancellationToken) =>
            WindowsRunningApplicationCatalog.ReadProcessTree(cancellationToken);
    }

    private static class NativeMethods
    {
        public const uint Th32csSnapProcess = 0x00000002;
        public const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint DwmWindowAttributeCloaked = 14;
        private const uint GetWindowOwner = 4;
        private const int ExtendedWindowStyle = -20;
        public static readonly IntPtr InvalidHandleValue = new(-1);

        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr window,
            uint attribute,
            out int value,
            int valueSize);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr window, out Rect bounds);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        public static TaskbarWindowSnapshot ReadTaskbarWindowSnapshot(IntPtr window)
        {
            var isVisible = IsWindowVisible(window);
            var isCloaked = DwmGetWindowAttribute(
                    window,
                    DwmWindowAttributeCloaked,
                    out var cloaked,
                    sizeof(int)) == 0 &&
                cloaked != 0;
            var extendedStyle = IntPtr.Size == 8
                ? GetWindowLongPtr64(window, ExtendedWindowStyle).ToInt64()
                : GetWindowLong32(window, ExtendedWindowStyle);

            return new TaskbarWindowSnapshot(
                isVisible,
                isCloaked,
                GetWindow(window, GetWindowOwner) != IntPtr.Zero,
                extendedStyle);
        }

        public static bool IsTopLevel(IntPtr window) => GetAncestor(window, 2) == window;

        public static Rect ReadNormalWindowBounds(IntPtr window)
        {
            var placement = new WindowPlacement
            {
                Length = Marshal.SizeOf<WindowPlacement>()
            };
            return GetWindowPlacement(window, ref placement)
                ? placement.NormalPosition
                : default;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCommand;
            public Point MinimumPosition;
            public Point MaximumPosition;
            public Rect NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableFile;
        }
    }
}

internal interface IWindowCatalogNativeAccess
{
    bool IsSupported { get; }
    long GetForegroundWindow();
    IReadOnlyList<long> EnumerateWindows(CancellationToken cancellationToken);
    WindowCatalogSnapshot? ReadWindow(long handle);
    string? ReadExecutablePath(int processId);
    IReadOnlyDictionary<int, ProcessTreeEntry> ReadProcessTree(CancellationToken cancellationToken);
}

internal sealed record WindowCatalogSnapshot(
    long Handle,
    int ProcessId,
    string Title,
    string WindowClass,
    TaskbarWindowSnapshot Taskbar,
    int ClientWidth,
    int ClientHeight,
    int RestoredWidth = 0,
    int RestoredHeight = 0,
    bool IsMinimized = false,
    bool IsTopLevel = true);
