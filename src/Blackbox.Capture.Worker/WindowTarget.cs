using System.Runtime.InteropServices;

namespace Blackbox.Capture.Worker;

internal sealed record WindowTarget(long Handle, int ProcessId)
{
    public static WindowTarget Validate(long handle, int processId, bool requireAvailable)
    {
        if (handle == 0 || handle == GetDesktopWindow().ToInt64() || handle == GetShellWindow().ToInt64())
            throw new ArgumentException("An explicit application HWND is required; desktop capture is not supported.");
        if (!IsWindow((nint)handle))
            throw new ArgumentException("The capture HWND no longer exists.");
        GetWindowThreadProcessId((nint)handle, out var actualPid);
        if (actualPid == 0 || (processId != 0 && processId != actualPid))
            throw new ArgumentException("The HWND does not belong to the supplied process ID.");
        var target = new WindowTarget(handle, checked((int)actualPid));
        if (requireAvailable && target.GetDiagnostic() is { } diagnostic)
            throw new ArgumentException(diagnostic);
        return target;
    }

    public string? GetDiagnostic()
    {
        if (!IsWindow((nint)Handle)) return "target_closed: waiting for an explicit target command";
        GetWindowThreadProcessId((nint)Handle, out var actualPid);
        if (actualPid != ProcessId) return "target_reused: HWND process identity changed";
        if (IsIconic((nint)Handle)) return "target_minimized: recording is paused until restored";
        if (!IsWindowVisible((nint)Handle)) return "target_hidden: recording is paused until visible";
        if (DwmGetWindowAttribute((nint)Handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return "target_cloaked: recording is paused until visible";
        if (!GetClientRect((nint)Handle, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
            return "target_empty: capture target has no drawable area";
        return null;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
}
