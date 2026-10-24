using System.Windows;
using SidebarPrototype.Shell;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    internal void ApplyUtilityToolWindowStyle(Window window, string name)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var before = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        if ((before.ToInt64() & NativeMethods.WsExToolWindow) == 0)
        {
            NativeMethods.SetWindowLongPtr(
                hwnd,
                NativeMethods.GwlExStyle,
                new nint(before.ToInt64() | NativeMethods.WsExToolWindow)
            );
            NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0037);
        }
#if TEST_HARNESS
        if (_options.TransitionTest)
            RecordTransitionWindowStyle(
                name,
                hwnd,
                before,
                NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle)
            );
#endif
    }

    private void Reserve(double dock, double panel)
    {
#if TEST_HARNESS
        if (_options.TransitionTest && !_options.TransitionNative)
        {
            RecordMockReservation(dock, panel);
            return;
        }
#endif
        if (!_options.BrowserOnly)
            _shell.SetReservation(dock, panel);
    }

    private void CommitPanelResize()
    {
        if (!_state.IsVisible || _state.ActiveId is not { } id || _panel.ActualWidth <= 0)
            return;
        var setting = GetSetting(id);
        var width = PanelSizing.Clamp(_panel.ActualWidth, SystemParameters.PrimaryScreenWidth, _settings.DockWidth);
        var reservationChanged = Math.Abs(_openWidth - width) > 1;
        _openWidth = width;
        var layoutRaisedByReservation = reservationChanged && _openMode == PanelMode.Split;
        if (layoutRaisedByReservation)
            Reserve(_settings.DockWidth, width);
        if (Math.Abs(setting.Width - width) > 1)
        {
            setting.Width = width;
            _ = SaveAsync();
        }
        if (
            !layoutRaisedByReservation
            || _options.BrowserOnly
            || (_options.TransitionTest && !_options.TransitionNative)
        )
            ApplyLayout();
        if (setting.MobileMode)
            ScheduleActiveMobileViewportRefresh();
    }

    private bool _applyingLayout;

    private void ApplyLayout()
    {
        if (!IsLoaded || _applyingLayout)
            return;
#if TEST_HARNESS
        var caller = _options.TransitionTest
            ? new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name
            : null;
#endif
        _layoutApplicationCount++;
        _applyingLayout = true;
        try
        {
            if (_state.IsVisible)
            {
                var bounded = PanelSizing.Clamp(_openWidth, SystemParameters.PrimaryScreenWidth, _settings.DockWidth);
                if (Math.Abs(bounded - _openWidth) > 1)
                {
                    _openWidth = bounded;
                    if (_openMode == PanelMode.Split)
                        Reserve(_settings.DockWidth, bounded);
                }
            }
            var layout =
                _options.TransitionTest && !_options.TransitionNative
                    ? new DockLayout(
                        new Rect(-12000, 0, _settings.DockWidth, 952),
                        new Rect(-12000 + _settings.DockWidth, 0, 1600, 952)
                    )
                : _options.BrowserOnly
                    ? new DockLayout(new Rect(-12000, 0, _settings.DockWidth, 952), new Rect(-11800, 0, 1400, 952))
                : _shell.GetLayout();
            Left = layout.DockBounds.Left;
            Top = layout.DockBounds.Top;
            Height = layout.DockBounds.Height;
            Width = _settings.DockWidth;
            _panel.Topmost = _settings.Topmost && !_shell.IsFullscreenActive;
            _panel.MaxWidth = PanelSizing.Clamp(
                double.MaxValue,
                SystemParameters.PrimaryScreenWidth,
                _settings.DockWidth
            );
            _panel.MinWidth = Math.Min(320, _panel.MaxWidth);
            if (_state.IsVisible)
                _panel.ApplyLayout(
                    _options.LayoutOnly
                        ? layout with
                        {
                            PanelBounds = new Rect(-11000, 0, layout.PanelBounds.Width, layout.PanelBounds.Height),
                        }
                        : layout,
                    _openWidth
                );
#if TEST_HARNESS
            if (_options.TransitionTest)
                RecordLayoutApply(caller);
#endif
        }
        finally
        {
            _applyingLayout = false;
        }
    }
}
