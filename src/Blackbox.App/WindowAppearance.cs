using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Blackbox.App;

internal static class WindowAppearance
{
    public static void ApplyDarkTitleBar(WindowInteropHelper helper)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(helper.Handle, 20, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(helper.Handle, 19, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);
}
