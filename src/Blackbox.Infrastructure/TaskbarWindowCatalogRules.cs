using Blackbox.Domain;

namespace Blackbox.Infrastructure;

internal readonly record struct TaskbarWindowSnapshot(
    bool IsVisible,
    bool IsCloaked,
    bool HasOwner,
    long ExtendedStyle);

internal static class TaskbarWindowCatalogRules
{
    internal const long ToolWindowStyle = 0x00000080L;
    internal const long AppWindowStyle = 0x00040000L;

    public static bool IsEligible(TaskbarWindowSnapshot window)
    {
        if (!window.IsVisible || window.IsCloaked)
        {
            return false;
        }

        if ((window.ExtendedStyle & AppWindowStyle) != 0)
        {
            return true;
        }

        return !window.HasOwner &&
               (window.ExtendedStyle & ToolWindowStyle) == 0;
    }

    public static bool IsLikelyVisibleGameSurface(
        TaskbarWindowSnapshot window,
        bool hasWindowTitle,
        int width,
        int height,
        GameDetectionSource detectionSources) =>
        window.IsVisible &&
        !window.IsCloaked &&
        hasWindowTitle &&
        width >= 320 &&
        height >= 180 &&
        (detectionSources & (GameDetectionSource.SteamLibrary | GameDetectionSource.SteamProcessTree)) != 0;

    public static IReadOnlyList<RunningApplication> Order(IEnumerable<RunningApplication> applications) =>
        applications
            .GroupBy(
                static application =>
                    application.WindowHandle != 0
                        ? $"{application.ProcessId}\0HWND:{application.WindowHandle}"
                        : $"{application.ProcessId}\0{application.ObsWindowIdentifier}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static application => application.IsForeground)
                .First())
            .OrderByDescending(static application => application.IsForeground)
            .ThenBy(static application => application.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static application => application.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static application => application.ProcessId)
            .ThenBy(static application => application.WindowHandle)
            .ToArray();

    public static (int Width, int Height) ResolveWindowSize(
        int clientWidth,
        int clientHeight,
        int restoredWidth,
        int restoredHeight) =>
        clientWidth > 0 && clientHeight > 0
            ? (clientWidth, clientHeight)
            : (Math.Max(0, restoredWidth), Math.Max(0, restoredHeight));
}
