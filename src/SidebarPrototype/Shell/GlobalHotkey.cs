using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace SidebarPrototype.Shell;

/// <summary>Registers one process-wide shortcut against a WPF window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private static int _nextId = 0x4A00;
    private readonly int _id = Interlocked.Increment(ref _nextId);
    private readonly Window _window;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? Pressed;

    public GlobalHotkey(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Unable to attach hotkey to the window HWND.");
        _source.AddHook(WindowProc);
    }

    /// <param name="modifiers">MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN and optionally MOD_NOREPEAT.</param>
    public bool TryRegister(uint modifiers, uint virtualKey, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        error = null;
        if (virtualKey == 0) { error = "Choose a valid shortcut key."; return false; }
        Unregister();
        if (NativeMethods.RegisterHotKey(new WindowInteropHelper(_window).Handle, _id, modifiers, virtualKey))
        {
            _registered = true;
            return true;
        }
        var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        error = code == 1409 ? "That shortcut is already registered by another application." : new Win32Exception(code).Message;
        return false;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    private void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(new WindowInteropHelper(_window).Handle, _id);
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unregister();
        _source.RemoveHook(WindowProc);
        _disposed = true;
    }
}
