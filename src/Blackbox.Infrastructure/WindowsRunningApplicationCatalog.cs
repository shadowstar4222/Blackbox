using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class WindowsRunningApplicationCatalog(
    ILogger<WindowsRunningApplicationCatalog> logger) : IRunningApplicationCatalog
{
    public Task<IReadOnlyList<RunningApplication>> GetRunningApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<RunningApplication>>([]);
        }

        var applications = new List<RunningApplication>();
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var processTree = ReadProcessTree();

        NativeMethods.EnumWindows((window, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            TryAddWindow(applications, window, foregroundWindow, processTree);
            return true;
        }, IntPtr.Zero);

        cancellationToken.ThrowIfCancellationRequested();
        var distinct = applications
            .GroupBy(application => application.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(application => application.IsForeground)
                .ThenByDescending(application => (long)application.WindowWidth * application.WindowHeight)
                .First())
            .OrderByDescending(application => application.IsForeground)
            .ThenBy(application => application.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<RunningApplication>>(distinct);
    }

    private void TryAddWindow(
        ICollection<RunningApplication> applications,
        IntPtr window,
        IntPtr foregroundWindow,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree)
    {
        if (!NativeMethods.IsWindowVisible(window))
        {
            return;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return;
        }

        try
        {
            var title = ReadWindowText(window);
            var windowClass = ReadWindowClass(window);
            var executablePath = ReadExecutablePath(processId);
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(windowClass) ||
                string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            executablePath = Path.GetFullPath(executablePath);
            var executableName = Path.GetFileName(executablePath);
            if (GameCandidateSelector.IsIgnoredExecutable(executableName) ||
                IsWindowsSystemExecutable(executablePath) ||
                !NativeMethods.GetClientRect(window, out var bounds))
            {
                return;
            }

            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width < 320 || height < 200)
            {
                return;
            }

            var isForeground = window == foregroundWindow;
            applications.Add(new RunningApplication(
                (int)processId,
                executablePath,
                executableName,
                title,
                $"{title}:{windowClass}:{executableName}",
                width,
                height,
                isForeground,
                GameCandidateSelector.Classify((int)processId, executablePath, isForeground, processTree))
            {
                AncestorExecutableNames = GetAncestorExecutableNames((int)processId, processTree)
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            logger.LogDebug(ex, "Could not inspect window for process {ProcessId}.", processId);
        }
    }

    private static bool IsWindowsSystemExecutable(string executablePath)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return executablePath.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadExecutablePath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var mainModulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath))
            {
                return mainModulePath;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }

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

    private static IReadOnlyDictionary<int, ProcessTreeEntry> ReadProcessTree()
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
                entries[(int)entry.ProcessId] = new ProcessTreeEntry(
                    (int)entry.ProcessId,
                    (int)entry.ParentProcessId,
                    entry.ExecutableFile);
                entry.Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

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
        var length = NativeMethods.GetWindowTextLength(window);
        var buffer = new StringBuilder(Math.Max(1, length + 1));
        _ = NativeMethods.GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static class NativeMethods
    {
        public const uint Th32csSnapProcess = 0x00000002;
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public static readonly IntPtr InvalidHandleValue = new(-1);

        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr window, out Rect bounds);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowTextLength(IntPtr window);

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

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
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
