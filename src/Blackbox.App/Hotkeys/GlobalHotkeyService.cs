using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace Blackbox.App.Hotkeys;

[SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "The HwndSource is borrowed from the WPF window; this service only owns and removes its hook.")]
public sealed class GlobalHotkeyService(ILogger<GlobalHotkeyService> logger) : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly Dictionary<int, Func<Task>> _handlers = [];
    private HwndSource? _source;
    private IntPtr _windowHandle;

    public void Attach(WindowInteropHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _source?.RemoveHook(WndProc);
        _windowHandle = helper.Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);
    }

    public void Register(GlobalHotkey hotkey, Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        ArgumentNullException.ThrowIfNull(handler);
        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Hotkey service must be attached to a window before registration.");
        }

        if (!RegisterHotKey(_windowHandle, hotkey.Id, (uint)hotkey.Modifiers, hotkey.VirtualKey))
        {
            throw new InvalidOperationException($"Unable to register global hotkey {hotkey.Id}.");
        }

        _handlers[hotkey.Id] = handler;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
        {
            UnregisterHotKey(_windowHandle, id);
        }

        _handlers.Clear();
        _source?.RemoveHook(WndProc);
        _source = null;
        _windowHandle = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            _ = InvokeHandlerAsync(handler);
        }

        return IntPtr.Zero;
    }

    private async Task InvokeHandlerAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A Blackbox global hotkey action failed.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
