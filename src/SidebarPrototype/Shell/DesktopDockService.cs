using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SidebarPrototype.Shell;

/// <summary>Call EnablePerMonitorV2 before constructing WPF windows.</summary>
public static class ShellDpi
{
    private static readonly nint PerMonitorAwareV2 = new(-4);
    public static bool EnablePerMonitorV2() => NativeMethods.SetProcessDpiAwarenessContext(PerMonitorAwareV2);
}

/// <summary>Owns the dock AppBar registration. All public calls must run on the window dispatcher.</summary>
public sealed class DesktopDockService : IDisposable
{
    private const int CallbackMessage = NativeMethods.WmApp + 0x35A;
    private const int SpiSetWorkArea = 0x002F;
    private const int WsExTopmost = 0x00000008;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private readonly uint _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private Window? _dock;
    private HwndSource? _source;
    private nint _hwnd;
    private double _dockWidthDip = 45;
    private double _panelWidthDip;
    private bool _registered;
    private bool _disposed;
    private bool _handlingPosition;
    private bool _topmost = true;
    private bool _fullscreen;
    private NativeMethods.Rect _reservedRect;
    private long _appBarSetPosCallCount;

    public event EventHandler? LayoutChanged;
    public bool IsFullscreenActive => _fullscreen;
    public bool IsReserved => _registered;
    /// <summary>Number of ABM_SETPOS calls made by this service, for runtime diagnostics.</summary>
    public long AppBarSetPosCallCount => _appBarSetPosCallCount;

    public void Attach(Window dock)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dock);
        if (_dock is not null) throw new InvalidOperationException("A dock window is already attached.");
        _dock = dock;
        _hwnd = new WindowInteropHelper(dock).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("Unable to attach to the dock HWND.");
        _source.AddHook(WindowProc);
        dock.SourceInitialized += OnSourceInitialized;
        dock.Closed += OnDockClosed;
        UpdateFullscreenState();
    }

    public void SetReservation(double dockWidthDip, double panelWidthDip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dock is null) throw new InvalidOperationException("Attach a dock window first.");
        if (!double.IsFinite(dockWidthDip) || dockWidthDip <= 0) throw new ArgumentOutOfRangeException(nameof(dockWidthDip));
        if (!double.IsFinite(panelWidthDip) || panelWidthDip < 0) throw new ArgumentOutOfRangeException(nameof(panelWidthDip));
        var reservationChanged = _dockWidthDip != dockWidthDip || _panelWidthDip != panelWidthDip;
        _dockWidthDip = dockWidthDip;
        _panelWidthDip = panelWidthDip;
        // Repeated visibility and layout updates commonly request the already active
        // reservation. Avoid renegotiating the global AppBar position in that case;
        // shell/display notifications below still force a fresh position calculation.
        if (!_registered || reservationChanged) RegisterOrReposition();
        if (_dock.IsVisible != true) NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShow);
        ApplyDockBounds();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Release()
    {
        if (_disposed) return;
        Unregister();
        if (_hwnd != 0 && NativeMethods.IsWindow(_hwnd)) NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
        _panelWidthDip = 0;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public DockLayout GetLayout()
    {
        if (_dock is null || _hwnd == 0) return new DockLayout(Rect.Empty, Rect.Empty);
        var monitor = GetPrimaryMonitor();
        var info = GetMonitorInfo(monitor);
        var dockWidthPx = DipToPixels(_dockWidthDip, _hwnd, monitor);
        var scale = GetDpi(_hwnd, monitor) / 96.0;
        var leftPx = _registered ? _reservedRect.Left : info.RcMonitor.Left;
        var topPx = _registered ? _reservedRect.Top : info.RcWork.Top;
        var bottomPx = _registered ? _reservedRect.Bottom : info.RcWork.Bottom;
        var dockRightPx = leftPx + dockWidthPx;
        var panelWidthPx = _panelWidthDip > 0
            ? Math.Min(DipToPixels(_panelWidthDip, _hwnd, monitor), Math.Max(0, info.RcMonitor.Right - dockRightPx))
            : Math.Max(0, info.RcMonitor.Right - dockRightPx);
        // WPF DIPs are physical pixels scaled by this primary monitor's DPI. PMv2 native logical
        // points remain physical pixels, so use the explicit monitor scale here.
        var left = leftPx / scale;
        var top = topPx / scale;
        var dockRight = dockRightPx / scale;
        var bottom = bottomPx / scale;
        var dock = new Rect(left, top, dockWidthPx / scale, bottom - top);
        var panel = new Rect(dockRight, top, panelWidthPx / scale, bottom - top);
        return new DockLayout(dock, panel);
    }

    internal static (Rect Dock, Rect Panel) ComputeRects(NativeMethods.Rect monitor, NativeMethods.Rect work, double dockWidthPx, double panelWidthPx, double scale)
    {
        var left = (double)monitor.Left;
        var top = (double)work.Top;
        var height = Math.Max(0, work.Bottom - work.Top) / scale;
        var dockWidth = Math.Max(0, dockWidthPx) / scale;
        var panelWidth = Math.Max(0, panelWidthPx) / scale;
        return (new Rect(left / scale, top / scale, dockWidth, height), new Rect((left + dockWidthPx) / scale, top / scale, panelWidth, height));
    }

    public void SetTopmost(bool enabled)
    {
        _topmost = enabled;
        ApplyTopmost();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) { if (_registered) RegisterOrReposition(); }
    private void OnDockClosed(object? sender, EventArgs e) => Release();

    private void RegisterOrReposition()
    {
        if (_hwnd == 0 || !NativeMethods.IsWindow(_hwnd)) return;
        var data = CreateAppBarData();
        var newlyRegistered = false;
        _handlingPosition = true;
        try
        {
            if (!_registered)
            {
                var result = NativeMethods.SHAppBarMessage(NativeMethods.AbmNew, ref data);
                if (result == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected AppBar registration.");
                _registered = true;
                newlyRegistered = true;
            }
            Reposition(data, forceSetPos: newlyRegistered);
        }
        finally { _handlingPosition = false; }
        SetTopmost(_topmost);
    }

    private void Reposition(NativeMethods.AppBarData data, bool forceSetPos = false)
    {
        var monitor = GetPrimaryMonitor();
        var info = GetMonitorInfo(monitor);
        var widthPx = DipToPixels(_dockWidthDip + _panelWidthDip, _hwnd, monitor);
        var wasHandlingPosition = _handlingPosition;
        _handlingPosition = true;
        try
        {
            data.Edge = NativeMethods.AbeLeft;
            data.Rc = new NativeMethods.Rect { Left = info.RcMonitor.Left, Top = info.RcWork.Top, Right = info.RcMonitor.Left + widthPx, Bottom = info.RcWork.Bottom };
            NativeMethods.SHAppBarMessage(NativeMethods.AbmQueryPos, ref data);
            data.Rc.Right = data.Rc.Left + widthPx;
            if (forceSetPos || !RectsEqual(data.Rc, _reservedRect))
            {
                _appBarSetPosCallCount++;
                NativeMethods.SHAppBarMessage(NativeMethods.AbmSetPos, ref data);
            }
            _reservedRect = data.Rc;
            ApplyDockBounds(_reservedRect);
        }
        finally { _handlingPosition = wasHandlingPosition; }
    }

    private void ApplyDockBounds() => ApplyDockBounds(default);
    private void ApplyDockBounds(NativeMethods.Rect reserved)
    {
        if (_hwnd == 0 || !NativeMethods.IsWindow(_hwnd)) return;
        if (reserved.Right <= reserved.Left || reserved.Bottom <= reserved.Top)
        {
            if (_registered) reserved = _reservedRect;
            else
            {
                var monitor = GetPrimaryMonitor();
                var info = GetMonitorInfo(monitor);
                var width = DipToPixels(_dockWidthDip, _hwnd, monitor);
                reserved = new NativeMethods.Rect { Left = info.RcMonitor.Left, Top = info.RcWork.Top, Right = info.RcMonitor.Left + width, Bottom = info.RcWork.Bottom };
            }
        }
        var dockWidth = Math.Min(DipToPixels(_dockWidthDip, _hwnd, GetPrimaryMonitor()), Math.Max(0, reserved.Right - reserved.Left));
        var dockBounds = new NativeMethods.Rect { Left = reserved.Left, Top = reserved.Top, Right = reserved.Left + dockWidth, Bottom = reserved.Bottom };
        if (IsDockBoundsCurrent(dockBounds, EffectiveTopmost)) return;
        NativeMethods.SetWindowPos(_hwnd, EffectiveTopmost ? HwndTopmost : HwndNotTopmost, dockBounds.Left, dockBounds.Top, dockWidth, dockBounds.Bottom - dockBounds.Top, NativeMethods.SwpNoActivate);
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreatedMessage)
        {
            _registered = false;
            if (_panelWidthDip + _dockWidthDip > 0 && _dock?.IsVisible == true) RegisterOrReposition();
        }
        else if (message == CallbackMessage)
        {
            var notification = unchecked((int)wParam);
            if (notification == NativeMethods.AbnPosChanged && !_handlingPosition && _registered) Reposition(CreateAppBarData());
            else if (notification == NativeMethods.AbnFullscreenApp) SetFullscreenState(lParam != 0);
        }
        else if (message == NativeMethods.WmDisplayChange || message == NativeMethods.WmDpiChanged)
        {
            if (_registered && !_handlingPosition) Reposition(CreateAppBarData());
            UpdateFullscreenState();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (message == NativeMethods.WmSettingChange && unchecked((int)wParam) == SpiSetWorkArea)
        {
            // WM_SETTINGCHANGE is broadcast for many unrelated settings. Only an
            // explicit work-area change affects this AppBar's reserved geometry.
            if (_registered && !_handlingPosition) Reposition(CreateAppBarData());
            UpdateFullscreenState();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    private void UpdateFullscreenState()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var next = false;
        if (foreground != 0 && !ForegroundWindowHelper.IsOwnedByCurrentProcess(foreground) && NativeMethods.GetWindowRect(foreground, out var rect))
        {
            var mon = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MonitorDefaultToNearest);
            var mi = GetMonitorInfo(mon);
            next = rect.Left <= mi.RcMonitor.Left && rect.Top <= mi.RcMonitor.Top && rect.Right >= mi.RcMonitor.Right && rect.Bottom >= mi.RcMonitor.Bottom;
        }
        SetFullscreenState(next);
    }

    private void SetFullscreenState(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        ApplyTopmost();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private NativeMethods.AppBarData CreateAppBarData() => new() { CbSize = (uint)Marshal.SizeOf<NativeMethods.AppBarData>(), HWnd = _hwnd, CallbackMessage = CallbackMessage };
    private void Unregister()
    {
        if (!_registered) return;
        var data = CreateAppBarData();
        NativeMethods.SHAppBarMessage(NativeMethods.AbmRemove, ref data);
        _registered = false;
        _reservedRect = default;
    }
    private static NativeMethods.MonitorInfo GetMonitorInfo(nint monitor)
    {
        var info = new NativeMethods.MonitorInfo { CbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query display bounds.");
        return info;
    }
    private static uint GetDpi(nint hwnd, nint monitor)
    {
        var dpi = hwnd != 0 ? NativeMethods.GetDpiForWindow(hwnd) : 0u;
        return dpi == 0 ? 96 : dpi;
    }
    private static int DipToPixels(double dip, nint hwnd, nint monitor) => (int)Math.Round(dip * GetDpi(hwnd, monitor) / 96.0);
    private static nint GetPrimaryMonitor() => NativeMethods.MonitorFromPoint(new NativeMethods.Point(0, 0), NativeMethods.MonitorDefaultToPrimary);
    private bool EffectiveTopmost => _topmost && !_fullscreen;
    private void ApplyTopmost()
    {
        if (_hwnd == 0 || !NativeMethods.IsWindow(_hwnd) || HasTopmostState(EffectiveTopmost)) return;
        NativeMethods.SetWindowPos(_hwnd, EffectiveTopmost ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize);
    }

    private bool IsDockBoundsCurrent(NativeMethods.Rect desired, bool topmost)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var current)) return false;
        return RectsEqual(current, desired) && HasTopmostState(topmost);
    }

    private bool HasTopmostState(bool topmost)
    {
        var extendedStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle).ToInt64();
        return ((extendedStyle & WsExTopmost) != 0) == topmost;
    }

    private static bool RectsEqual(NativeMethods.Rect left, NativeMethods.Rect right) =>
        left.Left == right.Left && left.Top == right.Top && left.Right == right.Right && left.Bottom == right.Bottom;

    public void Dispose()
    {
        if (_disposed) return;
        Release();
        _disposed = true;
        if (_source is not null) _source.RemoveHook(WindowProc);
        if (_dock is not null) { _dock.SourceInitialized -= OnSourceInitialized; _dock.Closed -= OnDockClosed; }
        _source = null; _dock = null; _hwnd = 0;
    }
}
