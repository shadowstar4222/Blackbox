using Blackbox.Domain;

namespace Blackbox.Infrastructure;

internal readonly record struct TaskbarWindowSnapshot(
    bool IsVisible,
    bool IsCloaked,
    bool IsRootOwnerLastActivePopup,
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

        return window.IsRootOwnerLastActivePopup &&
               !window.HasOwner &&
               (window.ExtendedStyle & ToolWindowStyle) == 0;
    }

    public static IReadOnlyList<RunningApplication> Order(IEnumerable<RunningApplication> applications) =>
        applications
            .OrderByDescending(static application => application.IsForeground)
            .ThenBy(static application => application.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static application => application.ExecutableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static application => application.ProcessId)
            .ToArray();

    public static (int Width, int Height) ResolveWindowSize(
        int clientWidth,
        int clientHeight,
        int restoredWidth,
        int restoredHeight) =>
        clientWidth > 0 && clientHeight > 0
            ? (clientWidth, clientHeight)
            : (Math.Max(1, restoredWidth), Math.Max(1, restoredHeight));
}
