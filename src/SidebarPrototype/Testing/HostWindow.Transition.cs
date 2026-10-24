using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SidebarPrototype.Shell;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    private sealed record WindowRectSnapshot(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPos
    {
        public nint Hwnd,
            HwndInsertAfter;
        public int X,
            Y,
            Cx,
            Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public int Length,
            Flags,
            ShowCmd;
        public NativeMethods.Point MinPosition,
            MaxPosition;
        public NativeMethods.Rect NormalPosition;
    }

    private sealed record NativePlacementSnapshot(int Left, int Top, int Right, int Bottom, int ShowCmd);

    private sealed record NativeWindowPosSnapshot(int X, int Y, int Width, int Height, uint Flags);

    private sealed record NativeBoundsEvent(
        long AtTick,
        string Window,
        int Message,
        bool Visible,
        string? Active,
        string Mode,
        double OpenWidth,
        int Dpi,
        long ExStyle,
        WindowRectSnapshot Rect,
        WindowRectSnapshot? OtherRect,
        double? WpfLeft,
        double? WpfTop,
        double? WpfWidth,
        double? WpfHeight,
        double? ShellDockLeft,
        double? ShellPanelLeft,
        WorkAreaSnapshot WorkArea,
        NativePlacementSnapshot? Placement,
        NativeWindowPosSnapshot? Proposed
    );

    private sealed record TransitionStep(
        string App,
        double Width,
        string Mode,
        int OpenLayoutDelta,
        long OpenReservationDelta,
        long CloseReservationDelta,
        WindowRectSnapshot? Rect
    );

    private sealed record TransitionSample(
        double Ms,
        bool TraceArmed,
        bool Visible,
        bool DockWindowVisible,
        bool DockRegistered,
        string? Active,
        string Mode,
        double OpenWidth,
        double? PanelWidth,
        double PanelActualWidth,
        double PanelHeight,
        int PanelDpi,
        int DockDpi,
        WindowRectSnapshot? PanelRect,
        WindowRectSnapshot? DockRect,
        WindowRectSnapshot? ApprovedDockRect,
        WorkAreaSnapshot WorkArea,
        long AppBarSetPosCalls,
        long MockReservations,
        int LayoutApplications,
        int PanelPositionMessages,
        int PanelMoveMessages,
        int PanelSizeMessages,
        int DockPositionMessages,
        int DockMoveMessages,
        int DockSizeMessages
    );

    internal bool TransitionTest => _options.TransitionTest;

    private long _mockReservationCount;
    private readonly List<object> _mockReservations = [];
    private readonly List<TransitionSample> _transitionSamples = [];
    private readonly List<NativeBoundsEvent> _nativeBoundsEvents = [];
    private readonly List<object> _layoutApplyEvents = [];
    private readonly List<object> _transitionWindowStyles = [];
    private System.Windows.Interop.HwndSource? _transitionSource;
    private int _transitionDockPosMessages;
    private int _transitionDockMoveMessages;
    private int _transitionDockSizeMessages;
    private long _transitionTraceStartTick;

    private void InstallTransitionWindowHook()
    {
        _transitionSource = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle
        );
        _transitionSource?.AddHook(TransitionWindowProc);
    }

    // The mocked transition run records reservations instead of moving the real desktop work area.
    private void RecordMockReservation(double dock, double panel)
    {
        _mockReservationCount++;
        _mockReservations.Add(
            new
            {
                atMs = Environment.TickCount64,
                dock,
                panel,
                active = _state.ActiveId,
                visible = _state.IsVisible,
            }
        );
    }

    private void RecordLayoutApply(string? caller)
    {
        var currentLayout = _shell.GetLayout();
        _layoutApplyEvents.Add(
            new
            {
                atTick = Environment.TickCount64,
                caller,
                active = _state.ActiveId,
                visible = _state.IsVisible,
                openMode = _openMode.ToString(),
                openWidth = _openWidth,
                workArea = RuntimeProbe.CaptureWorkArea(),
                shellDockLeft = Finite(currentLayout.DockBounds.Left),
                shellPanelLeft = Finite(currentLayout.PanelBounds.Left),
                wpfDockLeft = Finite(Left),
                wpfPanelLeft = Finite(_panel.Left),
                wpfPanelWidth = Finite(_panel.Width),
                actualPanelWidth = Finite(_panel.ActualWidth),
                appBarSetPosCalls = _shell.AppBarSetPosCallCount,
            }
        );
    }

    internal void RecordTransitionWindowStyle(string window, nint hwnd, nint before, nint after) =>
        _transitionWindowStyles.Add(
            new
            {
                window,
                hwnd = hwnd.ToInt64(),
                before = before.ToInt64(),
                after = after.ToInt64(),
                wasToolWindow = (before.ToInt64() & NativeMethods.WsExToolWindow) != 0,
                isToolWindow = (after.ToInt64() & NativeMethods.WsExToolWindow) != 0,
            }
        );

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private nint TransitionWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0046)
            RecordNativeBounds("dock", hwnd, message, lParam);
        else if (message == 0x0047)
        {
            _transitionDockPosMessages++;
            RecordNativeBounds("dock", hwnd, message);
        }
        else if (message == 0x0003)
        {
            _transitionDockMoveMessages++;
            RecordNativeBounds("dock", hwnd, message);
        }
        else if (message == 0x0005)
        {
            _transitionDockSizeMessages++;
            RecordNativeBounds("dock", hwnd, message);
        }
        return 0;
    }

    internal void RecordNativeBounds(string window, nint hwnd, int message, nint lParam = default)
    {
        if (!_options.TransitionTest || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return;
        var otherHwnd = new System.Windows.Interop.WindowInteropHelper(window == "panel" ? this : _panel).Handle;
        WindowRectSnapshot? otherRect =
            otherHwnd != 0 && NativeMethods.GetWindowRect(otherHwnd, out var other)
                ? new WindowRectSnapshot(other.Left, other.Top, other.Right, other.Bottom)
                : null;
        Window target = window == "panel" ? _panel : this;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        NativePlacementSnapshot? placement = null;
        var nativePlacement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
        if (GetWindowPlacement(hwnd, ref nativePlacement))
        {
            var r = nativePlacement.NormalPosition;
            placement = new(r.Left, r.Top, r.Right, r.Bottom, nativePlacement.ShowCmd);
        }
        NativeWindowPosSnapshot? proposed = null;
        if (message == 0x0046 && lParam != 0)
        {
            var pos = Marshal.PtrToStructure<NativeWindowPos>(lParam);
            proposed = new(pos.X, pos.Y, pos.Cx, pos.Cy, pos.Flags);
        }
        var shellLayout = _shell.GetLayout();
        var workArea = RuntimeProbe.CaptureWorkArea();
        _nativeBoundsEvents.Add(
            new NativeBoundsEvent(
                Environment.TickCount64,
                window,
                message,
                NativeMethods.IsWindowVisible(hwnd),
                _state.ActiveId,
                _openMode.ToString(),
                _openWidth,
                checked((int)NativeMethods.GetDpiForWindow(hwnd)),
                exStyle.ToInt64(),
                new WindowRectSnapshot(rect.Left, rect.Top, rect.Right, rect.Bottom),
                otherRect,
                Finite(target.Left),
                Finite(target.Top),
                Finite(target.Width),
                Finite(target.Height),
                Finite(shellLayout.DockBounds.Left),
                Finite(shellLayout.PanelBounds.Left),
                workArea,
                placement,
                proposed
            )
        );
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref NativePlacement placement);

    private async Task RunTransitionRegressionAsync()
    {
        var results = new Dictionary<string, object?>
        {
            ["mode"] =
                _options.TransitionToolWindow ? "real-webview-transition-native-appbar-toolwindow"
                : _options.TransitionNative ? "real-webview-transition-with-native-appbar"
                : "real-webview-transition-with-mocked-appbar",
            ["startedUtc"] = DateTime.UtcNow,
        };
        long ReservationCount() => _options.TransitionNative ? _shell.AppBarSetPosCallCount : _mockReservationCount;
        var baseArea = _workAreaBefore ?? RuntimeProbe.CaptureWorkArea();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var panelSizeEvents = 0;
        var panelLocationEvents = 0;
        var dockSizeEvents = 0;
        var dockLocationEvents = 0;
        _panel.SizeChanged += (_, _) => panelSizeEvents++;
        _panel.LocationChanged += (_, _) => panelLocationEvents++;
        SizeChanged += (_, _) => dockSizeEvents++;
        LocationChanged += (_, _) =>
        {
            dockLocationEvents++;
            if (_options.TransitionTest)
                RecordNativeBounds("dock", new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x0003);
        };
        WindowRectSnapshot? Bounds(Window window)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            return hwnd != 0 && NativeMethods.GetWindowRect(hwnd, out var rect)
                ? new WindowRectSnapshot(rect.Left, rect.Top, rect.Right, rect.Bottom)
                : null;
        }
        void Sample()
        {
            if (_transitionSamples.Count >= 2500)
                return;
            var p = Bounds(_panel);
            var d = Bounds(this);
            var panelHwnd = new System.Windows.Interop.WindowInteropHelper(_panel).Handle;
            var dockHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var panelDpi = panelHwnd != 0 ? checked((int)NativeMethods.GetDpiForWindow(panelHwnd)) : 0;
            var dockDpi = dockHwnd != 0 ? checked((int)NativeMethods.GetDpiForWindow(dockHwnd)) : 0;
            var approvedLayout = _shell.GetLayout().DockBounds;
            var scale = dockDpi / 96.0;
            var approvedDock =
                approvedLayout.IsEmpty || dockDpi <= 0
                    ? null
                    : new WindowRectSnapshot(
                        (int)Math.Round(approvedLayout.Left * scale),
                        (int)Math.Round(approvedLayout.Top * scale),
                        (int)Math.Round(approvedLayout.Right * scale),
                        (int)Math.Round(approvedLayout.Bottom * scale)
                    );
            _transitionSamples.Add(
                new TransitionSample(
                    sw.Elapsed.TotalMilliseconds,
                    _transitionTraceStartTick != 0 && Environment.TickCount64 >= _transitionTraceStartTick,
                    _state.IsVisible,
                    NativeMethods.IsWindowVisible(dockHwnd),
                    _shell.IsReserved,
                    _state.ActiveId,
                    _openMode.ToString(),
                    _openWidth,
                    Finite(_panel.Width),
                    _panel.ActualWidth,
                    _panel.ActualHeight,
                    panelDpi,
                    dockDpi,
                    p,
                    d,
                    approvedDock,
                    RuntimeProbe.CaptureWorkArea(),
                    _shell.AppBarSetPosCallCount,
                    _mockReservationCount,
                    _layoutApplicationCount,
                    _panel.WindowPositionMessages,
                    _panel.WindowMoveMessages,
                    _panel.WindowSizeMessages,
                    _transitionDockPosMessages,
                    _transitionDockMoveMessages,
                    _transitionDockSizeMessages
                )
            );
        }
        var sampler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
        sampler.Tick += (_, _) => Sample();
        try
        {
            foreach (var l in Launchers.All)
            {
                var s = GetSetting(l.Id);
                s.Mode = l.Id.StartsWith("calendar", StringComparison.Ordinal) ? PanelMode.Overlay : PanelMode.Split;
                s.Width = l.Id switch
                {
                    "gmail-a" => 500,
                    "gmail-b" => 564,
                    "soundcloud" => 605,
                    _ => 0,
                };
                s.MobileMode = l.Id == "soundcloud";
                s.Zoom = l.Id switch
                {
                    "gmail-a" => .67,
                    "soundcloud" => .6,
                    _ => 1,
                };
            }
            Topmost = false;
            _panel.Topmost = false;
            sampler.Start();
            var first = Launchers.All[0];
            var staleOpen = ActivateAsync(first, false);
            await Task.Delay(8);
            await ActivateAsync(first, true);
            await staleOpen.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(250);
            Sample();
            var canceledOpenStayedClosed = !_state.IsVisible && !_panel.IsVisible;
            results["canceledActivationDidNotReopenAfterSameIconClose"] = canceledOpenStayedClosed;

            foreach (var l in Launchers.All)
            {
                await ActivateAsync(l, false);
                await WaitDocumentAsync(_views[l.Id]);
                await Dispatcher.Yield(DispatcherPriority.Render);
                Sample();
            }
            if (_state.IsVisible)
                ClosePanel(false);
            await Task.Delay(100);
            var viewRefs = _views.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var navStarts = 0;
            foreach (var view in _views.Values)
                view.CoreWebView2.NavigationStarting += (_, _) => navStarts++;
            var baselineNavStarts = navStarts;
            var initialLayouts = _layoutApplicationCount;
            var initialReservations = ReservationCount();
            var panelMessagesBefore = (
                _panel.WindowPositionMessages,
                _panel.WindowMoveMessages,
                _panel.WindowSizeMessages
            );
            var panelBoundsAtCheckpoints = new List<TransitionStep>();
            async Task OpenCloseAsync(Launcher l)
            {
                var beforeOpenLayouts = _layoutApplicationCount;
                var beforeOpenReservations = ReservationCount();
                await ActivateAsync(l, false);
                await Dispatcher.Yield(DispatcherPriority.Render);
                Sample();
                var openWidth = _openWidth;
                var openMode = _openMode;
                await Task.Delay(24);
                var beforeCloseReservations = ReservationCount();
                await ActivateAsync(l, true);
                await Dispatcher.Yield(DispatcherPriority.Render);
                Sample();
                panelBoundsAtCheckpoints.Add(
                    new TransitionStep(
                        l.Id,
                        openWidth,
                        openMode.ToString(),
                        _layoutApplicationCount - beforeOpenLayouts,
                        beforeCloseReservations - beforeOpenReservations,
                        ReservationCount() - beforeCloseReservations,
                        Bounds(_panel)
                    )
                );
                await Task.Delay(24);
            }
            _transitionTraceStartTick = Environment.TickCount64;
            if (_options.TransitionToolWindow)
            {
                // Focused native A/B: open and close the same Split Gmail panel twice.
                await OpenCloseAsync(Launchers.All.Single(x => x.Id == "gmail-a"));
                await OpenCloseAsync(Launchers.All.Single(x => x.Id == "gmail-a"));
            }
            else
            {
                foreach (
                    var id in new[]
                    {
                        "gmail-a",
                        "gmail-a",
                        "gmail-b",
                        "soundcloud",
                        "gmail-a",
                        "soundcloud",
                        "gmail-b",
                    }
                )
                    await OpenCloseAsync(Launchers.All.Single(x => x.Id == id));
            }

            var switchStartReservations = ReservationCount();
            long switchAfterOpenReservations = switchStartReservations,
                switchBeforeCloseReservations = switchStartReservations,
                switchCloseReservationDelta = 0;
            double switchStartWidth = 0;
            var switchStartMode = PanelMode.Split;
            var noExtraReservationForOpenSwitches = true;
            var openGeometryPreserved = true;
            if (!_options.TransitionToolWindow)
            {
                await ActivateAsync(Launchers.All[0], false);
                switchAfterOpenReservations = ReservationCount();
                switchStartWidth = _openWidth;
                switchStartMode = _openMode;
                foreach (var index in new[] { 2, 4, 1, 3, 0 })
                {
                    await ActivateAsync(Launchers.All[index], false);
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    Sample();
                    if (_panel.IsVisible)
                        await WaitDocumentAsync(_views[Launchers.All[index].Id]);
                }
                switchBeforeCloseReservations = ReservationCount();
                noExtraReservationForOpenSwitches =
                    switchAfterOpenReservations - switchStartReservations == 1
                    && switchBeforeCloseReservations - switchAfterOpenReservations == 0;
                openGeometryPreserved =
                    Math.Abs(_openWidth - switchStartWidth) < .5
                    && _openMode == switchStartMode
                    && Math.Abs(_panel.ActualWidth - switchStartWidth) < 2;
                await ActivateAsync(Launchers.All[0], true);
                await Dispatcher.Yield(DispatcherPriority.Render);
                Sample();
                switchCloseReservationDelta = ReservationCount() - switchBeforeCloseReservations;
            }
            var expectedPerStepDelta = _options.TransitionNative ? 0 : 1;
            var edgeReservationsMatch =
                panelBoundsAtCheckpoints.All(x =>
                    x.Mode == PanelMode.Split.ToString()
                        ? x.OpenReservationDelta == 1 && x.CloseReservationDelta == 1
                        : x.OpenReservationDelta == expectedPerStepDelta
                            && x.CloseReservationDelta == expectedPerStepDelta
                )
                && (
                    _options.TransitionToolWindow
                    || switchAfterOpenReservations - switchStartReservations == 1
                        && switchBeforeCloseReservations - switchAfterOpenReservations == 0
                        && switchCloseReservationDelta == 1
                );
            if (!_options.TransitionToolWindow)
            {
                await OpenCloseAsync(Launchers.All[1]);
                await OpenCloseAsync(Launchers.All[3]);
            }
            await Task.Delay(300);
            Sample();

            var geometryRows = _transitionSamples.Where(x => x.Visible).ToArray();
            var maxVisibleWidth = geometryRows.Length == 0 ? 0d : geometryRows.Max(x => x.PanelActualWidth);
            var maxWidthError =
                geometryRows.Length == 0
                    ? double.PositiveInfinity
                    : geometryRows.Max(x => Math.Abs(x.PanelActualWidth - x.OpenWidth));
            var noOversizeFrame = geometryRows.Length > 0 && maxWidthError <= 2;
            bool SampleBoundsMatch(TransitionSample x)
            {
                if (x.PanelRect is null || x.DockRect is null || x.PanelDpi <= 0 || x.DockDpi <= 0)
                    return false;
                var expectedPanel = (int)Math.Round(x.OpenWidth * x.PanelDpi / 96.0);
                var expectedDock = (int)Math.Round(_settings.DockWidth * x.DockDpi / 96.0);
                return Math.Abs(x.PanelRect.Width - expectedPanel) <= 2
                    && Math.Abs(x.DockRect.Width - expectedDock) <= 2
                    && x.PanelRect.Left == x.DockRect.Right
                    && x.PanelRect.Top == x.DockRect.Top
                    && x.PanelRect.Bottom == x.DockRect.Bottom;
            }
            bool EventBoundsMatch(NativeBoundsEvent x)
            {
                if (x.Dpi <= 0 || x.Window == "panel" && !x.Visible)
                    return true;
                var expected = (int)
                    Math.Round((x.Window == "panel" ? x.OpenWidth : _settings.DockWidth) * x.Dpi / 96.0);
                if (Math.Abs(x.Rect.Width - expected) > 2)
                    return false;
                // During WM_WINDOWPOSCHANGING the companion HWND can be between its own AppBar moves.
                // Compare the event's HWND with the authoritative layout coordinates instead of that transient rect.
                var expectedLeft = x.Window == "panel" ? x.ShellPanelLeft : x.ShellDockLeft;
                var expectedTop = x.WorkArea.Top;
                if (
                    expectedLeft is null
                    || Math.Abs(x.Rect.Left - (int)Math.Round(expectedLeft.Value * x.Dpi / 96.0)) > 2
                    || x.Rect.Top != expectedTop
                )
                    return false;
                if (x.Proposed is { } pending)
                {
                    if (
                        (pending.Flags & 0x0002) == 0
                        && (
                            Math.Abs(pending.X - (int)Math.Round(expectedLeft.Value * x.Dpi / 96.0)) > 2
                            || pending.Y != expectedTop
                        )
                    )
                        return false;
                    if (
                        (pending.Flags & 0x0001) == 0
                        && (
                            Math.Abs(pending.Width - expected) > 2
                            || Math.Abs(pending.Height - (x.WorkArea.Bottom - x.WorkArea.Top)) > 2
                        )
                    )
                        return false;
                }
                return true;
            }
            var nativeSampleGeometryMatches = geometryRows.Length > 0 && geometryRows.All(SampleBoundsMatch);
            var dockRows = _transitionSamples
                .Where(x => x.TraceArmed && x.DockRegistered && x.DockWindowVisible)
                .ToArray();
            var closedDockRows = dockRows.Where(x => !x.Visible).ToArray();
            bool DockSampleMatches(TransitionSample x) =>
                x.DockRect is not null
                && x.ApprovedDockRect is not null
                && x.DockDpi > 0
                && Math.Abs(x.DockRect.Left - x.ApprovedDockRect.Left) <= 2
                && Math.Abs(x.DockRect.Top - x.ApprovedDockRect.Top) <= 2
                && Math.Abs(x.DockRect.Width - x.ApprovedDockRect.Width) <= 2
                && Math.Abs(x.DockRect.Height - x.ApprovedDockRect.Height) <= 2;
            var nativeDockSampleGeometryMatches = dockRows.Length > 0 && dockRows.All(DockSampleMatches);
            var closedDockGeometryMatches = closedDockRows.Length > 0 && closedDockRows.All(DockSampleMatches);
            var checkedNativeEvents = _nativeBoundsEvents
                .Where(x => x.AtTick >= _transitionTraceStartTick && (x.Window == "dock" || x.Visible))
                .ToArray();
            var nativeEventGeometryMatches =
                checkedNativeEvents.Length > 0 && checkedNativeEvents.All(EventBoundsMatch);
            var controllersStable = viewRefs.All(pair =>
                _views.TryGetValue(pair.Key, out var view) && ReferenceEquals(pair.Value, view)
            );
            var finalSourcesUnchanged = viewRefs.All(pair =>
                _views[pair.Key].CoreWebView2.Source == pair.Value.CoreWebView2.Source
            );
            var noNavigationOnSwitch = navStarts == baselineNavStarts;
            var workAreaUnchanged = !_options.TransitionNative && RuntimeProbe.CaptureWorkArea() == baseArea;
            var expectedReservationDelta =
                (
                    _options.TransitionNative
                        ? panelBoundsAtCheckpoints.Count(x => x.Mode == PanelMode.Split.ToString()) * 2
                        : panelBoundsAtCheckpoints.Count() * 2
                ) + (_options.TransitionToolWindow ? 0 : 2);
            var exactReservationTransitions =
                edgeReservationsMatch && ReservationCount() - initialReservations == expectedReservationDelta;
            results["controllerInstancesStableAcrossToggles"] = controllersStable;
            results["sourcesUnchangedAfterWarmup"] = finalSourcesUnchanged;
            results["noNavigationDuringTransitionSequences"] = noNavigationOnSwitch;
            results["openAppSwitchDoesNotRenegotiateReservation"] = noExtraReservationForOpenSwitches;
            results["switchReservationDeltas"] = new
            {
                open = switchAfterOpenReservations - switchStartReservations,
                duringSwitches = switchBeforeCloseReservations - switchAfterOpenReservations,
                close = switchCloseReservationDelta,
            };
            results["openSwitchPreservesPanelModeAndWidth"] = openGeometryPreserved;
            results[
                _options.TransitionNative ? "nativeAppBarSetPosTransitions" : "expectedMockReservationRequestsOnly"
            ] = new
            {
                passed = exactReservationTransitions,
                expected = expectedReservationDelta,
                actual = ReservationCount() - initialReservations,
                edgeReservationsMatch,
                switchOpen = switchAfterOpenReservations - switchStartReservations,
                switches = switchBeforeCloseReservations - switchAfterOpenReservations,
                switchClose = switchCloseReservationDelta,
            };
            results["noVisibleOversizePanelFrame"] = new
            {
                passed = noOversizeFrame,
                maxVisibleWidth,
                maxWidthError,
                visibleSamples = geometryRows.Length,
            };
            results["nativeGeometryAssertions"] = new
            {
                passed = nativeSampleGeometryMatches
                    && nativeDockSampleGeometryMatches
                    && closedDockGeometryMatches
                    && nativeEventGeometryMatches,
                allVisiblePanelSamplesMatch = nativeSampleGeometryMatches,
                allRegisteredDockSamplesMatch = nativeDockSampleGeometryMatches,
                closedDockSamplesMatch = closedDockGeometryMatches,
                allArmedPositionEventsMatch = nativeEventGeometryMatches,
                visiblePositionEvents = checkedNativeEvents.Length,
                dockSamples = dockRows.Length,
                closedDockSamples = closedDockRows.Length,
                visibleEventsBeforeTraceArm = _nativeBoundsEvents.Count(x =>
                    x.Visible && x.AtTick < _transitionTraceStartTick
                ),
                panelDpi = geometryRows.Select(x => x.PanelDpi).Distinct().ToArray(),
                dockDpi = geometryRows.Select(x => x.DockDpi).Distinct().ToArray(),
            };
            results["workAreaUnchanged"] = workAreaUnchanged;
            results["hostMovementAndLayoutEvents"] = new
            {
                panelSizeEvents,
                panelLocationEvents,
                dockSizeEvents,
                dockLocationEvents,
                layoutApplications = _layoutApplicationCount - initialLayouts,
                panelPositionMessages = _panel.WindowPositionMessages - panelMessagesBefore.WindowPositionMessages,
                panelMoveMessages = _panel.WindowMoveMessages - panelMessagesBefore.WindowMoveMessages,
                panelSizeMessages = _panel.WindowSizeMessages - panelMessagesBefore.WindowSizeMessages,
                dockPositionMessages = _transitionDockPosMessages,
                dockMoveMessages = _transitionDockMoveMessages,
                dockSizeMessages = _transitionDockSizeMessages,
            };
            results["steps"] = panelBoundsAtCheckpoints;
            results["contentMetrics"] = new
            {
                gmailA = await EvalAsync(
                    _views["gmail-a"],
                    "JSON.stringify({inner:innerWidth,screen:screen.width,scroll:document.documentElement.scrollWidth,client:document.documentElement.clientWidth})"
                ),
                gmailB = await EvalAsync(
                    _views["gmail-b"],
                    "JSON.stringify({inner:innerWidth,screen:screen.width,scroll:document.documentElement.scrollWidth,client:document.documentElement.clientWidth})"
                ),
                soundcloud = await EvalAsync(
                    _views["soundcloud"],
                    "JSON.stringify({inner:innerWidth,screen:screen.width,scroll:document.documentElement.scrollWidth,client:document.documentElement.clientWidth})"
                ),
            };
            results["appBarMode"] = _options.TransitionNative
                ? "native AppBar; isolated reservation released before completion"
                : "mocked to avoid changing the desktop reservation";
            results["status"] =
                canceledOpenStayedClosed
                && noExtraReservationForOpenSwitches
                && openGeometryPreserved
                && exactReservationTransitions
                && noOversizeFrame
                && nativeSampleGeometryMatches
                && nativeDockSampleGeometryMatches
                && closedDockGeometryMatches
                && nativeEventGeometryMatches
                && controllersStable
                && finalSourcesUnchanged
                && noNavigationOnSwitch
                && (_options.TransitionNative || workAreaUnchanged)
                    ? "completed"
                    : "failed";
            if (results["status"]?.ToString() != "completed")
                throw new InvalidOperationException("Transition geometry, lifecycle or reservation assertion failed");
        }
        catch (Exception error)
        {
            results["status"] = "failed";
            results["error"] = error.GetType().Name;
            results["message"] = error.Message;
        }
        finally
        {
            sampler.Stop();
            if (_state.IsVisible)
                ClosePanel(false);
            _shell.Release();
            _transitionSource?.RemoveHook(TransitionWindowProc);
        }
        results["samples"] = _transitionSamples;
        results["mockReservations"] = _mockReservations;
        if (_options.TransitionNative)
            await Task.Delay(500);
        var releasedArea = RuntimeProbe.CaptureWorkArea();
        var workAreaRestored = releasedArea == baseArea;
        results["workAreaBeforeInitialReservation"] = baseArea;
        results["workAreaAfterRelease"] = releasedArea;
        results["finalWorkAreaRestored"] = workAreaRestored;
        results["elapsedMs"] = sw.Elapsed.TotalMilliseconds;
        results["nativeBoundsEvents"] = _nativeBoundsEvents;
        results["layoutApplyEvents"] = _layoutApplyEvents;
        results["panelWindowStyleChanges"] = _transitionWindowStyles;
        if (_options.TransitionNative)
            results["status"] =
                results["status"]?.ToString() == "completed" && workAreaRestored ? "completed" : "failed";
        await File.WriteAllTextAsync(
            Path.Combine(_root, "transition-evidence.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
        );
        Quit(results["status"]?.ToString() == "completed" ? 0 : 1);
    }
}
