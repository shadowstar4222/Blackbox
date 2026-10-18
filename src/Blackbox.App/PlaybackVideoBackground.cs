using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Blackbox.App;

// WPF's background does not paint the native HWND exposed between VLC inputs.
internal sealed class PlaybackVideoBackground : IDisposable
{
    private readonly HwndHost host;

    public PlaybackVideoBackground(HwndHost host)
    {
        this.host = host;
        host.MessageHook += PaintBackground;
    }

    internal int NativePaintCount { get; private set; }

    private nint PaintBackground(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x0014 or 0x0318) // WM_ERASEBKGND / WM_PRINTCLIENT
        {
            if (message == 0x0014) NativePaintCount++;
            PaintBlack(window, wParam);
            handled = true;
            return 1;
        }
        if (message == 0x000F) // WM_PAINT
        {
            NativePaintCount++;
            var dc = BeginPaint(window, out var paint);
            try { PaintBlack(window, dc); }
            finally { EndPaint(window, ref paint); }
            handled = true;
        }
        return 0;
    }

    private static void PaintBlack(nint window, nint dc)
    {
        if (dc != 0 && GetClientRect(window, out var bounds))
            FillRect(dc, ref bounds, GetStockObject(4)); // BLACK_BRUSH is owned by Windows.
    }

    public void Dispose() => host.MessageHook -= PaintBackground;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint Dc;
        public int Erase;
        public NativeRect Bounds;
        public int Restore, IncUpdate;
        public int Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint window, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(nint window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, ref NativeRect bounds, nint brush);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);
}
