using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class WindowsRunningApplicationCatalog(
    IProcessExecutablePathResolver processPathResolver,
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
        return Task.FromResult(TaskbarWindowCatalogRules.Order(applications));
    }

    private void TryAddWindow(
        ICollection<RunningApplication> applications,
        IntPtr window,
        IntPtr foregroundWindow,
        IReadOnlyDictionary<int, ProcessTreeEntry> processTree)
    {
        var snapshot = NativeMethods.ReadTaskbarWindowSnapshot(window);
        if (!TaskbarWindowCatalogRules.IsEligible(snapshot))
        {
            return;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (threadId == 0 || processId == 0 || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            var title = ReadWindowText(window);
            var windowClass = ReadWindowClass(window);
            var processEntry = processTree.GetValueOrDefault((int)processId);
            var executablePath = ReadExecutablePath(processId) ??
                processPathResolver.Resolve((int)processId, processEntry?.ExecutableName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(windowClass) ||
                string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            executablePath = Path.GetFullPath(executablePath);
            var executableName = Path.GetFileName(executablePath);
            if (executableName.Equals("Blackbox.App.exe", StringComparison.OrdinalIgnoreCase) ||
                !NativeMethods.GetClientRect(window, out var bounds))
            {
                return;
            }

            var normalBounds = NativeMethods.ReadNormalWindowBounds(window);
            var (width, height) = TaskbarWindowCatalogRules.ResolveWindowSize(
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                normalBounds.Right - normalBounds.Left,
                normalBounds.Bottom - normalBounds.Top);

            title = string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(executableName)
                : title;
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
        private const uint DwmWindowAttributeCloaked = 14;
        private const uint GetWindowOwner = 4;
        private const uint GetAncestorRootOwner = 3;
        private const int ExtendedWindowStyle = -20;
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
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetLastActivePopup(IntPtr window);

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
                IsRootOwnerLastActivePopup(window),
                GetWindow(window, GetWindowOwner) != IntPtr.Zero,
                extendedStyle);
        }

        private static bool IsRootOwnerLastActivePopup(IntPtr window)
        {
            var current = GetAncestor(window, GetAncestorRootOwner);
            if (current == IntPtr.Zero)
            {
                current = window;
            }

            while (true)
            {
                var candidate = GetLastActivePopup(current);
                if (candidate == current)
                {
                    break;
                }

                if (IsWindowVisible(candidate))
                {
                    break;
                }

                current = candidate;
            }

            return current == window;
        }

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
