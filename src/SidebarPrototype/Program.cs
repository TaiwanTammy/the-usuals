using System.Net;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SidebarPrototype.Shell;
using SidebarPrototype.Verification;
using System.Windows.Threading;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using Control = System.Windows.Controls.Control;

namespace SidebarPrototype;

public sealed class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    [STAThread] public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.Run();
        ShellDpi.EnablePerMonitorV2();
        var app = new App();
        app.Startup += (_, _) => { try { app.Start(args); } catch (Exception e) { app.ReportStartupFailure(e, args); app.Shutdown(1); } };
        app.DispatcherUnhandledException += (_, e) => { app.ReportStartupFailure(e.Exception, args); (app.MainWindow as HostWindow)?.EmergencyCleanup(); e.Handled = true; app.Shutdown(1); };
        return app.Run();
    }
    private void Start(string[] args)
    {
        var dataDirArg = Array.FindIndex(args, x => x == "--data-dir");
        var isolated = dataDirArg >= 0 && dataDirArg + 1 < args.Length;
        var transitionTest = args.Contains("--transition-test") || args.Contains("--transition-native-test") || args.Contains("--transition-native-toolwindow-test");
        var transitionNative = args.Contains("--transition-native-test") || args.Contains("--transition-native-toolwindow-test");
        var transitionToolWindow = args.Contains("--transition-native-toolwindow-test");
        var unloadOnlyTest=args.Contains("--unload-only-test"); var uiTest=args.Contains("--ui-test");
        var fixture = args.Contains("--diagnostics") || args.Contains("--smoke-test") || args.Contains("--browser-test") || args.Contains("--layout-test") || unloadOnlyTest || transitionTest || uiTest;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarPrototype");
        var root = isolated ? Path.GetFullPath(args[dataDirArg + 1]) : Path.Combine(appData, fixture ? "diagnostics" : "");
        Directory.CreateDirectory(root);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var mutexName = "Local\\SidebarPrototype.Singleton." + (isolated || fixture ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..12] : "user");
        _instanceMutex = new Mutex(true, mutexName, out var owner);
        if (!owner) { _instanceMutex.Dispose(); _instanceMutex = null; if (!args.Contains("--smoke-test") && !transitionTest && !uiTest) System.Windows.MessageBox.Show("The sidebar is already running. Quit the current copy using its tray menu, then open this version.", "Sidebar", MessageBoxButton.OK, MessageBoxImage.Information); Shutdown(args.Contains("--smoke-test") || transitionTest || uiTest ? 2 : 0); return; }
        _ownsMutex = true;
        Exit += (_, _) => { if (_ownsMutex) _instanceMutex?.ReleaseMutex(); _instanceMutex?.Dispose(); _instanceMutex = null; };
        var settings = AppSettings.SafeLoad(Path.Combine(root, "settings.json"));
        try {
            var win = new HostWindow(root, settings, fixture, args.Contains("--smoke-test") || args.Contains("--layout-test") || transitionTest || unloadOnlyTest || uiTest, args.Contains("--browser-test") || uiTest, !args.Contains("--no-prewarm") && !transitionTest && !uiTest, args.Contains("--layout-test"), transitionTest, transitionNative, transitionToolWindow,unloadOnlyTest,uiTest);
            MainWindow = win;
            win.Show();
        } catch { if (_ownsMutex) { _instanceMutex?.ReleaseMutex(); _ownsMutex = false; _instanceMutex?.Dispose(); _instanceMutex = null; } throw; }
    }
    private void ReportStartupFailure(Exception error, string[] args)
    {
        var ix = Array.FindIndex(args, a => a == "--data-dir");
        var directory = ix >= 0 && ix + 1 < args.Length ? Path.GetFullPath(args[ix + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarPrototype");
        var diagnostic = args.Contains("--smoke-test") || args.Contains("--layout-test") || args.Contains("--transition-test") || args.Contains("--transition-native-test") || args.Contains("--transition-native-toolwindow-test") || args.Contains("--ui-test");
        try { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "startup-error.log"), $"{DateTimeOffset.UtcNow:O} {error.GetType().FullName}{Environment.NewLine}{error.StackTrace}"); if (diagnostic) File.WriteAllText(Path.Combine(directory, "startup-error-evidence.json"), JsonSerializer.Serialize(new { mode = "startup-diagnostic", status = "failed", error = error.GetType().Name })); } catch { }
        if (!diagnostic) System.Windows.MessageBox.Show("The sidebar could not start. Details were saved to the local diagnostic log.", AppBranding.TheUsuals, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

internal sealed class SelfTests
{
    public static int Run()
    {
        try {
            var gmail = Launchers.All[0];
            Assert(UrlPolicy.TryParseHttp("https://mail.google.com/", out var u) && UrlPolicy.IsInternal(u!, gmail), "recognized app host");
            Assert(!UrlPolicy.TryParseHttp("javascript:alert(1)", out _), "reject non-http");
            Assert(!UrlPolicy.TryParseHttp("https://mail.google.com.evil.test/", out var evil) || !UrlPolicy.IsInternal(evil!, gmail), "host boundary");
            Assert(!UrlPolicy.TryParseHttp("https://user:pass@mail.google.com/", out _), "reject credentials");
            Assert(UrlPolicy.IsAllowedRestore("https://mail.google.com/mail/u/0/", gmail) && !UrlPolicy.IsAllowedRestore("https://mail.google.com/oauth/callback?code=x", gmail) && !UrlPolicy.IsSafeRestorable("https://mail.google.com/#state=x") && !UrlPolicy.IsSafeRestorable("https://mail.google.com/?%63ode=one-time") && !UrlPolicy.IsSafeRestorable("https://mail.google.com/#%61ccess_token=secret"), "restore URL filtering");
            Assert(Launchers.All[0].Profile == Launchers.All[1].Profile && Launchers.All[0].Profile != Launchers.All[2].Profile, "profile mapping");
            Assert(PanelSizing.Initial(1440) == 480 && PanelSizing.Clamp(900, 800, 128) <= 352, "panel sizing");
            Assert(PanelSizing.Clamp(900, 350, 128) <= 222, "narrow sizing");
            Assert(AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com/signin/v2/identifier")) && AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com/o/oauth2/v2/auth")) && AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com/AccountChooser?continue=https%3A%2F%2Fcalendar.google.com")) && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com/AccountChooserEvil")) && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com.evil.example/signin")) && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("http://accounts.google.com/signin")) && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://www.google.com/signin")), "bounded Google popup auth intent");
            var state = new ViewState(); var old = state.BeginSwitch("gmail-a"); var latest = state.BeginSwitch("calendar-b");
            Assert(!state.CanPresent("gmail-a", old) && state.CanPresent("calendar-b", latest), "switch race");
            state.Hide(); Assert(!state.CanPresent("calendar-b", latest), "hide race");
            var test = new AppSettings { DockWidth = double.NaN, Launchers = new() { ["x"] = new() { Zoom = double.PositiveInfinity, Width = -1, LastUrl = "javascript:bad" } } }.Validate();
            Assert(test.DockWidth == 45 && test.Launchers["x"].Zoom == 1 && test.Launchers["x"].Width == 320 && test.Launchers["x"].LastUrl is null && new AppSettings { Launchers = new() { ["x"] = new() } }.Validate().Launchers["x"].Width == 0, "config sanitation");
            Console.WriteLine("PASS: URL policy, host boundary, config sanitation, sizing, profile mapping, view race state"); return 0;
        } catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.Message); return 1; }
    }
    private static void Assert(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); }
}

internal sealed partial class HostWindow : Window
{
    private sealed record WindowRectSnapshot(int Left,int Top,int Right,int Bottom) { public int Width=>Right-Left; public int Height=>Bottom-Top; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeWindowPos { public nint Hwnd,HwndInsertAfter; public int X,Y,Cx,Cy; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePlacement { public int Length,Flags,ShowCmd; public NativeMethods.Point MinPosition,MaxPosition; public NativeMethods.Rect NormalPosition; }
    private sealed record NativePlacementSnapshot(int Left,int Top,int Right,int Bottom,int ShowCmd);
    private sealed record NativeWindowPosSnapshot(int X,int Y,int Width,int Height,uint Flags);
    private sealed record NativeBoundsEvent(long AtTick,string Window,int Message,bool Visible,string? Active,string Mode,double OpenWidth,int Dpi,long ExStyle,WindowRectSnapshot Rect,WindowRectSnapshot? OtherRect,double? WpfLeft,double? WpfTop,double? WpfWidth,double? WpfHeight,double? ShellDockLeft,double? ShellPanelLeft,WorkAreaSnapshot WorkArea,NativePlacementSnapshot? Placement,NativeWindowPosSnapshot? Proposed);
    private sealed record TransitionStep(string App,double Width,string Mode,int OpenLayoutDelta,long OpenReservationDelta,long CloseReservationDelta,WindowRectSnapshot? Rect);
    private sealed record TransitionSample(double Ms,bool TraceArmed,bool Visible,bool DockWindowVisible,bool DockRegistered,string? Active,string Mode,double OpenWidth,double? PanelWidth,double PanelActualWidth,double PanelHeight,int PanelDpi,int DockDpi,
        WindowRectSnapshot? PanelRect,WindowRectSnapshot? DockRect,WindowRectSnapshot? ApprovedDockRect,WorkAreaSnapshot WorkArea,long AppBarSetPosCalls,long MockReservations,int LayoutApplications,
        int PanelPositionMessages,int PanelMoveMessages,int PanelSizeMessages,int DockPositionMessages,int DockMoveMessages,int DockSizeMessages);
    private readonly string _root; private readonly AppSettings _settings; private readonly bool _fixture, _smoke, _browserOnly, _prepareBrowsers, _layoutOnly, _transitionTest, _transitionNative, _transitionToolWindow, _unloadOnlyTest, _uiTest;
    private readonly DeferredSettingsWriter _settingsWriter;
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _mobileViewportTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<WebView2, (string Id, double Target)> _pendingZoom = [];
    private readonly ViewState _state = new(); private readonly Dictionary<string, WebView2> _views = [];
    private readonly Dictionary<WebView2,string> _viewOwners=[];
    private readonly Dictionary<string,long> _appEpochs=[];
    private readonly HashSet<string> _explicitlyUnloadedApps=[];
    private readonly Dictionary<string, string> _initializationErrors = [];
    private readonly Dictionary<string, NavigationSession> _navigationSessions = [];
    private readonly Dictionary<string, string> _desktopAgents = [];
    private readonly Dictionary<string, (int Width, int Height)> _mobileViewports = [];
    private int _mobileViewportUpdates;
    private int _layoutApplicationCount;
    private long _mockReservationCount;
    private readonly List<object> _mockReservations=[];
    private readonly List<TransitionSample> _transitionSamples=[];
    private readonly List<object> _transitionSteps=[];
    private readonly List<NativeBoundsEvent> _nativeBoundsEvents=[];
    private readonly List<object> _layoutApplyEvents=[];
    private readonly List<object> _transitionWindowStyles=[];
    private System.Windows.Interop.HwndSource? _transitionSource;
    private int _transitionDockPosMessages;
    private int _transitionDockMoveMessages;
    private int _transitionDockSizeMessages;
    private long _transitionTraceStartTick;
    private int _externalLaunchRequests;
    private PanelMode _openMode;
    private double _openWidth;
    private readonly Dictionary<string, Task> _initializers = [];
    private readonly PanelWindow _panel; private readonly DesktopDockService _shell; private readonly NotifyIcon _tray; private readonly GlobalHotkey _hotkey;
    private readonly Task<CoreWebView2Environment> _environment;
    private readonly DispatcherTimer _diagnosticTimer = new() { Interval=TimeSpan.FromMilliseconds(500) };
    private Task _diagnosticWrite = Task.CompletedTask;
    private readonly DispatcherTimer _focusWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private HttpListener? _fixtureServer; private string? _fixtureBase;
    public HostWindow(string root, AppSettings settings, bool fixture, bool smoke, bool browserOnly = false, bool prepareBrowsers = true, bool layoutOnly = false, bool transitionTest = false, bool transitionNative = false, bool transitionToolWindow = false,bool unloadOnlyTest=false,bool uiTest=false)
    {
        _root = root; _settings = settings; _fixture = fixture; _smoke = smoke; _browserOnly = browserOnly; _prepareBrowsers = prepareBrowsers; _layoutOnly = layoutOnly; _transitionTest=transitionTest; _transitionNative=transitionNative; _transitionToolWindow=transitionToolWindow; _unloadOnlyTest=unloadOnlyTest; _uiTest=uiTest; RefreshConfiguredLaunchers();
        _settingsWriter = new(settings, Path.Combine(root, "settings.json"));
        _diagnosticTimer.Tick += (_, _) => { _diagnosticTimer.Stop(); QueueDiagnosticWrite(); };
        _zoomTimer.Tick += (_, _) => ApplyPendingZoom();
        _mobileViewportTimer.Tick += async (_, _) => { _mobileViewportTimer.Stop(); await ApplyActiveMobileViewportAsync(); };
        Title = AppBranding.TheUsuals; Width = settings.DockWidth; Height = 480; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.Black; Topmost = settings.Topmost && !transitionTest; ShowInTaskbar = fixture && !browserOnly && !transitionTest; ShowActivated = !browserOnly && !layoutOnly && !transitionTest; if(transitionNative) Opacity=0;
        Content = _dock;
        _panel = new PanelWindow(this) { ShowInTaskbar = fixture && !browserOnly && !transitionTest, ShowActivated = !browserOnly && !layoutOnly && !transitionTest, Opacity=transitionNative ? 0 : 1 };
        _shell = new DesktopDockService(); _shell.LayoutChanged += (_, _) => ApplyLayout(); _shell.Attach(this);
        ApplyUtilityToolWindowStyle(this,"dock");
        if(_transitionTest) { _transitionSource=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle); _transitionSource?.AddHook(TransitionWindowProc); }
        if(transitionNative) _shell.SetTopmost(false); else if(!transitionTest) _shell.SetTopmost(settings.Topmost);
        _panel.Topmost = settings.Topmost && !transitionTest;
        _hotkey = new GlobalHotkey(this); string? hotkeyError = null; var hotkeyOk = transitionTest || _hotkey.TryRegister(settings.ShortcutModifiers, settings.ShortcutVirtualKey, out hotkeyError); _hotkey.Pressed += (_, _) => OnGlobalHotkey();
        _environment = CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "browser-data"));
        BuildButtons();
        _focusWatch.Tick += (_, _) => { if (_smoke || _panel.HasActiveAuth || !_state.IsVisible || _state.ActiveId is not { } active || _openMode != PanelMode.Overlay) return; var hwnd = ForegroundWindowHelper.GetForegroundWindow(); if (hwnd != 0 && !ForegroundWindowHelper.IsOwnedByCurrentProcess(hwnd)) ClosePanel(false); };
        _focusWatch.Start();
        _panel.UserResizeCompleted += (_, _) => CommitPanelResize();
        _tray = new NotifyIcon { Text = "Sidebar Prototype", Icon = System.Drawing.SystemIcons.Application, Visible = !transitionTest };
        var menu = new ContextMenuStrip(); menu.Items.Add("Show / Hide", null, (_, _) => ToggleVisible()); menu.Items.Add("Keyboard shortcut…", null, (_, _) => ConfigureShortcut()); menu.Items.Add("Quit sidebar", null, (_, _) => Quit()); if (!hotkeyOk) menu.Items.Add("Shortcut unavailable: " + (hotkeyError ?? "registration failed"), null, (_, _) => { }); _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => ToggleVisible();
        InitializeBranding();
        SourceInitialized += (_, _) => ApplyLayout();
        Loaded += (_, _) => { _workAreaBefore = RuntimeProbe.CaptureWorkArea(); _panel.Owner = this; _panel.EnsureNativeWindowConfigured(); Reserve(_settings.DockWidth, 0); if(_browserOnly || (_transitionTest && !_transitionNative)) ApplyLayout(); };
        Closing += (_, e) => { if (!_quitting) { e.Cancel = true; HideUtility(); } };
        if (_fixture && !_layoutOnly) StartFixture();
        if (_uiTest) Loaded += async (_,_)=>await RunUiRegressionAsync();
        else if (_unloadOnlyTest) Loaded += async (_,_)=>await RunUnloadOnlyAsync();
        else if (_smoke) Loaded += async (_, _) => { if(_layoutOnly) await RunLayoutRegressionAsync(); else if(_transitionTest) await RunTransitionRegressionAsync(); else await RunSmokeAsync(); };
        else if (_prepareBrowsers) Loaded += (_, _) => { _ = Dispatcher.BeginInvoke(new Action(async () => await PrepareEmptyViewsAsync()), DispatcherPriority.ApplicationIdle); };
    }
    private bool _quitting;
    internal bool IsQuitting => _quitting;
    internal bool TransitionTest => _transitionTest;
    internal bool TransitionToolWindow => _transitionToolWindow;
    internal void RecordTransitionWindowStyle(string window,nint hwnd,nint before,nint after) => _transitionWindowStyles.Add(new { window,hwnd=hwnd.ToInt64(),before=before.ToInt64(),after=after.ToInt64(),wasToolWindow=(before.ToInt64()&NativeMethods.WsExToolWindow)!=0,isToolWindow=(after.ToInt64()&NativeMethods.WsExToolWindow)!=0 });
    internal void ApplyUtilityToolWindowStyle(Window window,string name)
    {
        var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var before=NativeMethods.GetWindowLongPtr(hwnd,NativeMethods.GwlExStyle);
        if((before.ToInt64()&NativeMethods.WsExToolWindow)==0) { NativeMethods.SetWindowLongPtr(hwnd,NativeMethods.GwlExStyle,new nint(before.ToInt64()|NativeMethods.WsExToolWindow)); NativeMethods.SetWindowPos(hwnd,0,0,0,0,0,0x0037); }
        if(_transitionTest) RecordTransitionWindowStyle(name,hwnd,before,NativeMethods.GetWindowLongPtr(hwnd,NativeMethods.GwlExStyle));
    }
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    private nint _previousForeground;
    private string? _lastLauncherId;
    private WorkAreaSnapshot? _workAreaBefore;
    private void Reserve(double dock, double panel) { if(_transitionTest && !_transitionNative) { _mockReservationCount++; _mockReservations.Add(new { atMs=Environment.TickCount64,dock,panel,active=_state.ActiveId,visible=_state.IsVisible }); return; } if (!_browserOnly) _shell.SetReservation(dock, panel); }
    private async Task PrepareEmptyViewsAsync()
    {
        if (_quitting || _state.IsVisible) return;
        _panel.ShowActivated = false; _panel.Topmost = false; _panel.Left = -12000; _panel.Top = 0; _panel.Width = 500; _panel.Height = 700;
        _panel.Show();
        try {
            foreach (var launcher in Launchers.All) {
                if (_quitting || _state.IsVisible || _lastLauncherId is not null) break;
                if(_explicitlyUnloadedApps.Contains(launcher.Id)) continue;
                var epoch=AppEpoch(launcher.Id);
                EnsureView(launcher); _panel.SetActive(launcher, GetSetting(launcher.Id), _views[launcher.Id]);
                await Dispatcher.Yield(DispatcherPriority.Background);
                if(!IsAppEpochCurrent(launcher.Id,epoch)) continue;
                await StartInitialization(launcher, _views[launcher.Id]);
                if(!IsAppEpochCurrent(launcher.Id,epoch)) continue;
            }
        } finally { if (!_quitting) { if (!_state.IsVisible) _panel.Hide(); _panel.ShowActivated = !_browserOnly; } }
    }
    private readonly List<InteractionMetric> _interactionMetrics = [];
    private void RecordInteraction(string launcher, string operation, long started)
    {
        if (_interactionMetrics.Count >= 128) _interactionMetrics.RemoveAt(0);
        _interactionMetrics.Add(new(launcher, operation, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }
    private async Task ActivateFromDockAsync(Launcher launcher)
    {
        var epoch=AppEpoch(launcher.Id);
        try { await ActivateAsync(launcher); }
        catch(OperationCanceledException) when(!IsAppEpochCurrent(launcher.Id,epoch)) { }
        catch(ObjectDisposedException) when(!IsAppEpochCurrent(launcher.Id,epoch)) { }
        catch(Exception error) { if(IsAppEpochCurrent(launcher.Id,epoch) && _state.IsVisible && _state.ActiveId==launcher.Id) _panel.SetStatus($"Could not open {launcher.Label}: {error.GetType().Name}"); }
    }
    private async Task ActivateAsync(Launcher launcher, bool toggleSame = true)
    {
        var clickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (toggleSame && _state.IsVisible && _state.ActiveId == launcher.Id) { ClosePanel(!_transitionTest); return; }
        if(_explicitlyUnloadedApps.Remove(launcher.Id)) _appEpochs[launcher.Id]=AppEpoch(launcher.Id)+1;
        var wasOpen = _state.IsVisible;
        if (!wasOpen) _previousForeground = ForegroundWindowHelper.Capture();
        _lastLauncherId = launcher.Id;
        if (!IsVisible) Show();
        var generation = _state.BeginSwitch(launcher.Id);
        MarkActiveDockButton(launcher.Id);
        var setting = GetSetting(launcher.Id);
        if (!wasOpen) { _openMode = setting.Mode; _openWidth = GetPanelWidth(setting); Reserve(_settings.DockWidth, _openMode == PanelMode.Split ? _openWidth : 0); if(_browserOnly || (_transitionTest && !_transitionNative)) ApplyLayout(); }
        _panel.SetStatus($"Opening {launcher.Label}…"); _panel.Show(); if (!_browserOnly && !_layoutOnly && !_transitionTest) _panel.Activate();
        RecordInteraction(launcher.Id, "panel-shown", clickStarted);
        if (_layoutOnly) return;
        _ = Dispatcher.BeginInvoke(new Action(() => RecordInteraction(launcher.Id, "render-queue", clickStarted)), DispatcherPriority.Render);
        if (!_views.ContainsKey(launcher.Id)) await Dispatcher.Yield(DispatcherPriority.Background);
        if (_quitting || !_state.CanPresent(launcher.Id, generation)) return;
        EnsureView(launcher); var view = _views[launcher.Id];
        _panel.SetActive(launcher, setting, view); _panel.RefreshControls(setting,_openMode); RecordInteraction(launcher.Id, "view-created", clickStarted);
        await StartInitialization(launcher, view);
        RecordInteraction(launcher.Id, "controller-ready", clickStarted);
        if (_quitting || !_views.TryGetValue(launcher.Id, out var current) || !ReferenceEquals(current, view)) return;
        if (_initializationErrors.TryGetValue(launcher.Id, out var initializationError)) {
            if (_state.CanPresent(launcher.Id, generation)) { _panel.ShowFailure(launcher, () => _ = RetryAsync(launcher)); _panel.SetError(initializationError); _panel.SetStatus("Could not open this app. Use Retry."); }
            return;
        }
        if (view.CoreWebView2 is { } core) {
            var target = _fixture ? new Uri(_fixtureBase + launcher.Id + "/") : (UrlPolicy.IsAllowedRestore(setting.LastUrl, launcher) ? new Uri(setting.LastUrl!) : launcher.StartUri);
            if (_fixture) File.AppendAllText(Path.Combine(_root,"fixture-trace.log"), $"{launcher.Id} source={core.Source} target={target} panel={_panel.IsVisible},{_panel.ActualWidth},{_panel.ActualHeight}\n");
            if (string.IsNullOrWhiteSpace(core.Source) || core.Source == "about:blank" || AppReturnPolicy.IsGoogleLanding(launcher,core.Source)) core.Navigate(target.AbsoluteUri);
            RecordInteraction(launcher.Id, "navigation-requested", clickStarted);
            if (_state.CanPresent(launcher.Id, generation)) _panel.SetStatus(core.Source == "about:blank" ? $"Loading {launcher.Label}…" : DescribePage(launcher,core.Source));
        }
    }
    private void EnsureView(Launcher launcher)
    {
        if(_explicitlyUnloadedApps.Contains(launcher.Id)) return;
        if (_views.ContainsKey(launcher.Id)) return;
        var view = new WebView2 { Visibility = Visibility.Collapsed, DefaultBackgroundColor = System.Drawing.Color.FromArgb(23,23,23) }; _views[launcher.Id] = view; _viewOwners[view]=launcher.Id; _panel.AddView(view);
    }
    private Task StartInitialization(Launcher launcher, WebView2 view)
    {
        if (_initializers.TryGetValue(launcher.Id, out var existing)) return existing;
        var epoch=AppEpoch(launcher.Id);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _initializers[launcher.Id] = completion.Task;
        _ = InitializeAndCompleteAsync(); return completion.Task;
        async Task InitializeAndCompleteAsync() { try { await InitializeViewAsync(launcher, view,epoch); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } }
    }
    private async Task InitializeViewAsync(Launcher launcher, WebView2 view,long epoch)
    {
        try {
            var env = await _environment;
            if(!IsAppEpochCurrent(launcher.Id,epoch) || !IsCurrent(launcher,view)) return;
            view.CreationProperties = new CoreWebView2CreationProperties { ProfileName = launcher.Profile };
            await view.EnsureCoreWebView2Async(env);
            if(!IsAppEpochCurrent(launcher.Id,epoch) || !IsCurrent(launcher,view)) return;
            if(_fixture && UnloadRegressionMainControllerReadyHook is { } mainReady) await mainReady();
            if(!IsAppEpochCurrent(launcher.Id,epoch) || !IsCurrent(launcher,view)) return;
            await ConfigureMainViewAsync(launcher,view,true,epoch);

        } catch (Exception ex) { if (!IsAppEpochCurrent(launcher.Id,epoch) || !IsCurrent(launcher,view)) return; _initializationErrors[launcher.Id] = ex.Message; if (_fixture) File.AppendAllText(Path.Combine(_root,"fixture-trace.log"), ex.ToString()+"\n"); if (_state.IsVisible && _state.ActiveId == launcher.Id) { _panel.ShowFailure(launcher, () => _ = RetryAsync(launcher)); _panel.SetError(ex.Message); } }
    }
    private async Task RetryAsync(Launcher launcher)
    {
        _panel.ClearFailure();
        if (_views.Remove(launcher.Id, out var failed)) { _panel.RemoveView(failed); try { failed.Dispose(); } catch { } }
        _initializers.Remove(launcher.Id); _initializationErrors.Remove(launcher.Id); EnsureView(launcher); await ActivateAsync(launcher, false);
    }
    private async Task ConfigureKeyboardAsync(Launcher launcher, WebView2 view, CoreWebView2 core, AuthSurface? popup = null)
    {
        view.KeyDown += (_, e) => {
            var mods = e.KeyboardDevice.Modifiers;
            if (!mods.HasFlag(System.Windows.Input.ModifierKeys.Control) || mods.HasFlag(System.Windows.Input.ModifierKeys.Alt)) return;
            var next = e.Key switch {
                System.Windows.Input.Key.Add or System.Windows.Input.Key.OemPlus => Math.Min(2, GetSetting(launcher.Id).Zoom + .1),
                System.Windows.Input.Key.Subtract or System.Windows.Input.Key.OemMinus => Math.Max(.5, GetSetting(launcher.Id).Zoom - .1),
                System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0 => 1,
                _ => double.NaN
            };
            if (!double.IsFinite(next)) return;
            QueueZoom(launcher.Id, next, view); e.Handled = true;
        };
        core.WebMessageReceived += (_, e) => {
            string? message; try { message = e.TryGetWebMessageAsString(); } catch { return; }
            if (_quitting) return;
            if (message == AuthSurface.WindowCloseMessage) {
                _authCloseBridgeMessages++;
                // A popup's window.close is routed through this narrow message bridge so
                // WebView2's WPF wrapper cannot close the panel window that owns every view.
                if (popup is not null && !popup.IsPromoted) popup.Close();
                return;
            }
            if (message != "sidebar-unclaimed-escape") return;
            if (popup is not null && !popup.IsPromoted) popup.Close();
            else if (_state.IsVisible && _state.ActiveId == launcher.Id) ClosePanel();
        };
        var closeBridge = popup is null ? "" : $$"""
            if (window.top === window) {
                try {
                    Object.defineProperty(window, 'close', {
                        configurable: true,
                        value: function () { window.chrome.webview.postMessage('{{AuthSurface.WindowCloseMessage}}'); }
                    });
                } catch (_) { }
            }
            """;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(closeBridge + """
            document.addEventListener('keydown', e => {
                if (e.key !== 'Escape' || !e.isTrusted) return;
                const modal = !!document.querySelector('dialog[open], [role="dialog"], [role="alertdialog"]');
                setTimeout(() => {
                    if (!e.defaultPrevented && !modal) window.chrome.webview.postMessage('sidebar-unclaimed-escape');
                }, 0);
            }, true);
            """);
    }
    private void OpenExternal(Uri uri, bool explicitlyRequested = false) {
        if(!explicitlyRequested) { _externalSuppressed++; ObserveNavigation("outside-link",false,uri.AbsoluteUri,NavigationDecision.Block,false,false); _panel.OfferExternalLink(uri,()=>OpenExternal(uri,true)); return; }
        _externalLaunchRequests++; if (_fixture) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { _panel.SetStatus("The default browser could not open."); }
    }
    private int _externalSuppressed;
    private static string DescribePage(Launcher launcher, string uri) => UrlPolicy.TryParseHttp(uri,out var parsed) ? $"{launcher.Label} · {parsed!.IdnHost}" : launcher.Label;
    private readonly List<object> _navigationObservations = [];
    private int _authCloseBridgeMessages;
    private void ObserveNavigation(string id, bool popup, string? value, NavigationDecision decision, bool redirected, bool userInitiated, int? status = null) {
        var host = UrlPolicy.TryParseHttp(value,out var uri) ? uri!.IdnHost : "(non-http)";
        if (_navigationObservations.Count >= 128) _navigationObservations.RemoveAt(0);
        _navigationObservations.Add(new { id, popup, host, decision = decision.ToString(), redirected, userInitiated, status });
        _diagnosticTimer.Stop(); _diagnosticTimer.Start();
    }
    private void QueueDiagnosticWrite() {
            var snapshot=JsonSerializer.Serialize(new { build="update-6", externalLaunchRequests=_externalLaunchRequests, automaticExternalLaunchesSuppressed=_externalSuppressed, appBarSetPosCalls=_shell.AppBarSetPosCallCount, events=_navigationObservations.ToArray() },new JsonSerializerOptions { WriteIndented=true });
        _diagnosticWrite = _diagnosticWrite.ContinueWith(async previous => { _=previous.Exception; try { await File.WriteAllTextAsync(Path.Combine(_root,"navigation-diagnostics.json"),snapshot); } catch(IOException) { } catch(UnauthorizedAccessException) { } },TaskScheduler.Default).Unwrap();
    }
    internal void OnToolbar(string action)
    {
        var commandStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_state.ActiveId is not { } id || !_views.TryGetValue(id, out var v)) return;
        v = _panel.ActiveAuthView(id) ?? v;
        var s = GetSetting(id);
        switch (action) {
            case "close": ClosePanel(); break; case "back": if (v.CoreWebView2?.CanGoBack == true) v.CoreWebView2.GoBack(); break;
            case "forward": if (v.CoreWebView2?.CanGoForward == true) v.CoreWebView2.GoForward(); break; case "reload": v.CoreWebView2?.Reload(); break;
            case "mode": _openMode = _openMode == PanelMode.Overlay ? PanelMode.Split : PanelMode.Overlay; s.Mode = _openMode; Reserve(_settings.DockWidth, _openMode == PanelMode.Split ? _openWidth : 0); if(_browserOnly || (_transitionTest && !_transitionNative)) ApplyLayout(); _panel.RefreshControls(s,_openMode); _ = SaveAsync(); break;
            case "top": _settings.Topmost = !_settings.Topmost; Topmost = _settings.Topmost; _panel.Topmost = _settings.Topmost && !_shell.IsFullscreenActive; _shell.SetTopmost(_settings.Topmost); _panel.RefreshControls(s,_openMode); _ = SaveAsync(); break;
            case "theme": s.Theme = s.Theme == SiteTheme.Dark ? SiteTheme.Light : SiteTheme.Dark; var profile = ConfiguredLaunchers.Single(x => x.Id == id).Profile; foreach (var sibling in ConfiguredLaunchers.Where(x => x.Profile == profile)) GetSetting(sibling.Id).Theme = s.Theme; if (v.CoreWebView2 is { } core) core.Profile.PreferredColorScheme = s.Theme == SiteTheme.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light; _panel.RefreshControls(s,_openMode); _ = SaveAsync(); break;
            case "zoom+": QueueZoom(id, Math.Min(2, s.Zoom + .1),v); break;
            case "zoom-": QueueZoom(id, Math.Max(.5, s.Zoom - .1),v); break;
            case "zoom0": QueueZoom(id, 1,v); break;
            case "identity": _ = ToggleIdentityAsync(id, v); break;
            case "external": if(_panel.HasActiveAuth) { _panel.SetStatus("Finish or cancel sign-in in this panel first."); break; } if (UrlPolicy.TryParseHttp(v.Source?.AbsoluteUri, out var uri)) OpenExternal(uri!,true); break;
        }
        if (action.StartsWith("zoom")) RecordInteraction(id, "zoom-command", commandStarted);
    }
    private readonly HashSet<string> _identityChanges = [];
    private async Task ToggleIdentityAsync(string id, WebView2 view)
    {
        if(_panel.HasActiveAuth || _navigationSessions.TryGetValue(id,out var activeSession) && activeSession.AuthenticationActive) { _panel.SetStatus("Finish or cancel sign-in before changing browser mode."); return; }
        if (view.CoreWebView2 is not { } core || !_desktopAgents.TryGetValue(id,out var nativeAgent) || !_identityChanges.Add(id)) return;
        var epoch=AppEpoch(id);
        var setting = GetSetting(id); var mobile = !setting.MobileMode; var app=ConfiguredLaunchers.Single(x=>x.Id==id);
        var target=_fixture || UrlPolicy.IsAllowedRestore(core.Source,app) ? core.Source : app.StartUri.AbsoluteUri;
        try {
            core.Stop(); var viewport=(ViewportWidth(view),ViewportHeight(view)); await BrowserIdentity.ApplyAsync(core,mobile,nativeAgent,viewport.Item1,viewport.Item2); if(!IsAppEpochCurrent(id,epoch) || !IsCurrent(app,view)) return; setting.MobileMode = mobile;
            if(mobile) _mobileViewports[id]=viewport; else _mobileViewports.Remove(id);
            if (_state.IsVisible && _state.ActiveId == id) _panel.RefreshControls(setting,_openMode);
            await SaveAsync(); core.Navigate(target);
        } catch(OperationCanceledException) when(!IsAppEpochCurrent(id,epoch)) { }
        catch(ObjectDisposedException) when(!IsAppEpochCurrent(id,epoch)) { }
        catch { if (IsAppEpochCurrent(id,epoch) && _state.IsVisible && _state.ActiveId == id) _panel.SetStatus("Could not change browser mode. Please try again."); }
        finally { if(AppEpoch(id)==epoch) _identityChanges.Remove(id); }
    }
    private void QueueZoom(string id, double target, WebView2? source = null)
    {
        GetSetting(id).Zoom = target;
        if (_state.ActiveId == id) _panel.RefreshControls(GetSetting(id),_openMode);
        if (_views.TryGetValue(id, out var main)) _pendingZoom[main] = (id, target);
        if (source is not null) _pendingZoom[source] = (id, target);
        _panel.SetStatus($"Zoom {Math.Round(target * 100)}%"); _zoomTimer.Start(); _ = SaveAsync();
    }
    private void ApplyPendingZoom()
    {
        _zoomTimer.Stop(); var requests = _pendingZoom.ToArray(); _pendingZoom.Clear();
        foreach (var (view, request) in requests) {
            if (_quitting) return;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { view.ZoomFactor = request.Target; RecordInteraction(request.Id, "zoom-apply", started); } catch (ObjectDisposedException) { }
        }
    }
    private void ClosePanel(bool restoreFocus = true) {  _state.Hide(); MarkActiveDockButton(null); _panel.Hide(); if (IsVisible) Reserve(_settings.DockWidth, 0); if (restoreFocus && _previousForeground != 0) ForegroundWindowHelper.Restore(_previousForeground); _previousForeground = 0; _ = SaveAsync(); }
    private void HideUtility() { ClosePanel(); Hide(); _shell.Release(); }
    private void ToggleVisible() { if (IsVisible) HideUtility(); else { Show(); Reserve(_settings.DockWidth, 0); if(_browserOnly || (_transitionTest && !_transitionNative)) ApplyLayout(); Activate(); } }
    private void OnGlobalHotkey() { if (_state.IsVisible && ForegroundWindowHelper.IsOwnedByCurrentProcess(ForegroundWindowHelper.GetForegroundWindow())) ClosePanel(); else if (_lastLauncherId is { } id && ConfiguredLaunchers.FirstOrDefault(x => x.Id == id) is { } launcher) _ = ActivateAsync(launcher, false); else ToggleVisible(); }
    private void ConfigureShortcut()
    {
        var dialog = new ShortcutDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var oldMods = _settings.ShortcutModifiers; var oldKey = _settings.ShortcutVirtualKey;
        if (_hotkey.TryRegister(dialog.Modifiers, dialog.VirtualKey, out var error)) { _settings.ShortcutModifiers = dialog.Modifiers; _settings.ShortcutVirtualKey = dialog.VirtualKey; _ = SaveAsync(); }
        else { _hotkey.TryRegister(oldMods, oldKey, out _); System.Windows.MessageBox.Show(error ?? "Shortcut registration failed.", _brandName, MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void CommitPanelResize()
    {
        if (!_state.IsVisible || _state.ActiveId is not { } id || _panel.ActualWidth<=0) return;
        var setting=GetSetting(id); var width=PanelSizing.Clamp(_panel.ActualWidth,SystemParameters.PrimaryScreenWidth,_settings.DockWidth);
        var reservationChanged=Math.Abs(_openWidth-width)>1; _openWidth=width;
        var layoutRaisedByReservation=reservationChanged && _openMode==PanelMode.Split;
        if(layoutRaisedByReservation) Reserve(_settings.DockWidth,width);
        if(Math.Abs(setting.Width-width)>1) { setting.Width=width; _=SaveAsync(); }
        if(!layoutRaisedByReservation || _browserOnly || (_transitionTest && !_transitionNative)) ApplyLayout();
        if(setting.MobileMode) ScheduleActiveMobileViewportRefresh();
    }
    private bool _applyingLayout;
    private void ApplyLayout()
    {
        if(!IsLoaded || _applyingLayout) return;
        var caller=_transitionTest ? new StackTrace(1,false).GetFrame(0)?.GetMethod()?.Name : null;
        _layoutApplicationCount++;
        _applyingLayout=true; try {
        if(_state.IsVisible) {
            var bounded=PanelSizing.Clamp(_openWidth,SystemParameters.PrimaryScreenWidth,_settings.DockWidth);
            if(Math.Abs(bounded-_openWidth)>1) { _openWidth=bounded; if(_openMode==PanelMode.Split) Reserve(_settings.DockWidth,bounded); }
        }
        var layout=_transitionTest && !_transitionNative ? new DockLayout(new Rect(-12000,0,_settings.DockWidth,952),new Rect(-12000+_settings.DockWidth,0,1600,952)) : _browserOnly ? new DockLayout(new Rect(-12000,0,_settings.DockWidth,952),new Rect(-11800,0,1400,952)) : _shell.GetLayout();
        Left=layout.DockBounds.Left; Top=layout.DockBounds.Top; Height=layout.DockBounds.Height; Width=_settings.DockWidth;
        _panel.Topmost=_settings.Topmost && !_shell.IsFullscreenActive;
        _panel.MaxWidth=PanelSizing.Clamp(double.MaxValue,SystemParameters.PrimaryScreenWidth,_settings.DockWidth); _panel.MinWidth=Math.Min(320,_panel.MaxWidth);
        if(_state.IsVisible) _panel.ApplyLayout(_layoutOnly ? layout with { PanelBounds=new Rect(-11000,0,layout.PanelBounds.Width,layout.PanelBounds.Height) } : layout,_openWidth);
        if(_transitionTest) { var currentLayout=_shell.GetLayout(); _layoutApplyEvents.Add(new { atTick=Environment.TickCount64,caller,active=_state.ActiveId,visible=_state.IsVisible,openMode=_openMode.ToString(),openWidth=_openWidth,workArea=RuntimeProbe.CaptureWorkArea(),shellDockLeft=Finite(currentLayout.DockBounds.Left),shellPanelLeft=Finite(currentLayout.PanelBounds.Left),wpfDockLeft=Finite(Left),wpfPanelLeft=Finite(_panel.Left),wpfPanelWidth=Finite(_panel.Width),actualPanelWidth=Finite(_panel.ActualWidth),appBarSetPosCalls=_shell.AppBarSetPosCallCount }); }
        } finally { _applyingLayout=false; }
    }
    private nint TransitionWindowProc(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x0046) RecordNativeBounds("dock",hwnd,message,lParam);
        else if(message==0x0047) { _transitionDockPosMessages++; RecordNativeBounds("dock",hwnd,message); }
        else if(message==0x0003) { _transitionDockMoveMessages++; RecordNativeBounds("dock",hwnd,message); }
        else if(message==0x0005) { _transitionDockSizeMessages++; RecordNativeBounds("dock",hwnd,message); }
        return 0;
    }
    internal void RecordNativeBounds(string window,nint hwnd,int message,nint lParam=default)
    {
        if(!_transitionTest || !NativeMethods.GetWindowRect(hwnd,out var rect)) return;
        var otherHwnd=new System.Windows.Interop.WindowInteropHelper(window=="panel" ? this : _panel).Handle;
        WindowRectSnapshot? otherRect=otherHwnd!=0 && NativeMethods.GetWindowRect(otherHwnd,out var other) ? new WindowRectSnapshot(other.Left,other.Top,other.Right,other.Bottom) : null;
        Window target=window=="panel" ? _panel : this; var exStyle=NativeMethods.GetWindowLongPtr(hwnd,NativeMethods.GwlExStyle);
        NativePlacementSnapshot? placement=null; var nativePlacement=new NativePlacement { Length=Marshal.SizeOf<NativePlacement>() };
        if(GetWindowPlacement(hwnd,ref nativePlacement)) { var r=nativePlacement.NormalPosition; placement=new(r.Left,r.Top,r.Right,r.Bottom,nativePlacement.ShowCmd); }
        NativeWindowPosSnapshot? proposed=null;
        if(message==0x0046 && lParam!=0) { var pos=Marshal.PtrToStructure<NativeWindowPos>(lParam); proposed=new(pos.X,pos.Y,pos.Cx,pos.Cy,pos.Flags); }
        var shellLayout=_shell.GetLayout(); var workArea=RuntimeProbe.CaptureWorkArea();
        _nativeBoundsEvents.Add(new NativeBoundsEvent(Environment.TickCount64,window,message,NativeMethods.IsWindowVisible(hwnd),_state.ActiveId,_openMode.ToString(),_openWidth,checked((int)NativeMethods.GetDpiForWindow(hwnd)),exStyle.ToInt64(),new WindowRectSnapshot(rect.Left,rect.Top,rect.Right,rect.Bottom),otherRect,Finite(target.Left),Finite(target.Top),Finite(target.Width),Finite(target.Height),Finite(shellLayout.DockBounds.Left),Finite(shellLayout.PanelBounds.Left),workArea,placement,proposed));
    }
    [DllImport("user32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(nint hwnd,ref NativePlacement placement);
    private double GetPanelWidth(LauncherSetting s) { var width = s.Width > 0 ? s.Width : PanelSizing.Initial(SystemParameters.PrimaryScreenWidth); return PanelSizing.Clamp(width, SystemParameters.PrimaryScreenWidth, _settings.DockWidth); }
    private LauncherSetting GetSetting(string id) { if (!_settings.Launchers.TryGetValue(id, out var s)) _settings.Launchers[id] = s = new(); return s; }
    private Task SaveAsync() { _settingsWriter.Schedule(); return Task.CompletedTask; }
    private int _fixtureCallbackCount; private int _fixtureAppReturns;
    private void StartFixture()
    {
        var port = new Random().Next(18000, 49000); _fixtureBase = $"http://127.0.0.1:{port}/"; _fixtureServer = new HttpListener(); _fixtureServer.Prefixes.Add(_fixtureBase); _fixtureServer.Prefixes.Add($"http://localhost:{port}/"); _fixtureServer.Start();
        _ = Task.Run(async () => { while (_fixtureServer.IsListening) {
            HttpListenerContext ctx; try { ctx = await _fixtureServer.GetContextAsync(); } catch { return; }
            try {
                if (ctx.Request.Url!.AbsolutePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) { var wave = RuntimeProbe.CreateAudioWave(); ctx.Response.ContentType = "audio/wav"; ctx.Response.ContentLength64 = wave.Length; await ctx.Response.OutputStream.WriteAsync(wave); ctx.Response.Close(); continue; }
                if(ctx.Request.Url.AbsolutePath=="/app-return") Interlocked.Increment(ref _fixtureAppReturns);
                if(ctx.Request.Url.AbsolutePath=="/google-auth-intent") { ctx.Response.StatusCode=302; ctx.Response.RedirectLocation=_fixtureBase+"app-return"; ctx.Response.Close(); continue; }
                if (ctx.Request.Url.AbsolutePath == "/auth-post") { ctx.Response.StatusCode=307; ctx.Response.RedirectLocation=_fixtureBase+"auth-callback"; ctx.Response.Close(); continue; }
                if (ctx.Request.Url.AbsolutePath == "/main-redirect") { ctx.Response.StatusCode=302; ctx.Response.RedirectLocation=$"http://localhost:{port}/main-final"; ctx.Response.Close(); continue; }
                if (ctx.Request.Url.AbsolutePath == "/auth-callback") { using var reader=new StreamReader(ctx.Request.InputStream); var body=await reader.ReadToEndAsync(); if(ctx.Request.HttpMethod!="POST" || body!="fixture_nonce=one-use" || Interlocked.Increment(ref _fixtureCallbackCount)!=1) { ctx.Response.StatusCode=400; ctx.Response.Close(); continue; } }
                var authScript = ctx.Request.Url.AbsolutePath == "/auth-start" ? $"const form=document.createElement('form');form.method='POST';form.action='http://localhost:{port}/auth-post';const n=document.createElement('input');n.name='fixture_nonce';n.value='one-use';form.append(n);document.body.append(form);form.submit();" : ctx.Request.Url.AbsolutePath == "/auth-callback" ? "if(window.opener){window.opener.postMessage('fixture-auth-complete','*');setTimeout(()=>window.close(),250)}" : "";
                var popupScript = ctx.Request.Url.AbsolutePath == "/popup" ? "if(window.opener){window.opener.postMessage('fixture-popup-ok','*');window.opener.postMessage({closeBridge:window.close.toString().includes('sidebar-auth-surface-close')},'*');setTimeout(()=>window.close(),250)}" : "";
                var html = """<!doctype html><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1"><title>Fixture</title><style>body{font:18px Arial;background:#eee;padding:20px;height:2600px}@media(prefers-color-scheme:dark){body{background:#171717;color:#eee}}button{padding:10px}</style><h1>Sidebar compatibility fixture</h1><input id=input value='input-retention'><input type=file id=upload><button onclick="document.querySelector('#test-dialog').showModal()">Test dialog</button><dialog id=test-dialog><p>Fixture dialog</p><button onclick="document.querySelector('#test-dialog').close()">Dismiss</button></dialog><p>Profile storage marker: <b id=marker></b></p><p>Page instance: <b id=instance></b></p><p>Scroll, hide, switch and return. The instance id should remain stable.</p><button onclick="localStorage.marker='saved-'+location.pathname;document.querySelector('#marker').textContent=localStorage.marker">Write profile marker</button><button onclick="window.open('/popup','login-popup')">Open owned popup</button><audio id=audio controls loop src='/tone.wav'></audio><div style='height:1800px'></div><script>window.instanceId=crypto.randomUUID();document.querySelector('#instance').textContent=window.instanceId;document.querySelector('#marker').textContent=localStorage.marker||'(empty)';window.fixturePopup='';window.fixtureCloseBridge=false;window.addEventListener('message',e=>{if(typeof e.data==='string')window.fixturePopup=e.data;if(e.data&&typeof e.data.closeBridge==='boolean')window.fixtureCloseBridge=e.data.closeBridge});""" + popupScript + authScript + "window.requestAgent=" + JsonSerializer.Serialize(ctx.Request.UserAgent) + ";window.requestMobile=" + JsonSerializer.Serialize(ctx.Request.Headers["Sec-CH-UA-Mobile"]) + ";</script>";
                var bytes = Encoding.UTF8.GetBytes(html); ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.ContentLength64 = bytes.Length; await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
            } catch { try { ctx.Response.Abort(); } catch { } }
        } });
    }
    private async Task RunUnloadOnlyAsync()
    {
        var results=new Dictionary<string,object?> { ["mode"]="fixture-unload-regression",["startedUtc"]=DateTime.UtcNow };
        try {
            await PrepareEmptyViewsAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var gmail=Launchers.All.Single(x=>x.Id=="gmail-a"); await ActivateAsync(gmail,false).WaitAsync(TimeSpan.FromSeconds(20)); await WaitDocumentAsync(_views[gmail.Id]);
            var previousMarker=await EvalAsync(_views[gmail.Id],"localStorage.getItem('sidebar-unload-marker')");
            var previousCookie=await EvalAsync(_views[gmail.Id],"document.cookie");
            results["processStartRestoredGmailIdentity"] = new { zoom=_views[gmail.Id].ZoomFactor,configuredZoom=GetSetting(gmail.Id).Zoom,mobile=GetSetting(gmail.Id).MobileMode,agent=await EvalAsync(_views[gmail.Id],"navigator.userAgent.includes('Mobile')"),marker=JsonSerializer.Deserialize<string?>(previousMarker),cookie=previousCookie };
            await RunUnloadRegressionChecksAsync(results); await SaveAsync(); results["status"]="completed";
        }
        catch(Exception error) { results["status"]="failed"; results["error"]=error.GetType().Name; results["diagnosticMessage"]=error.Message; results["diagnosticStack"]=error.StackTrace; }
        await File.WriteAllTextAsync(Path.Combine(_root,"unload-evidence.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
        Quit(results["status"]?.ToString()=="completed" ? 0 : 1);
    }
    private async Task RunSmokeAsync()
    {
        var started = DateTime.UtcNow; var results = new Dictionary<string, object?> { ["mode"] = "fixture-smoke", ["startedUtc"] = started };
        void Stage(string stage) { results["stage"] = stage; File.WriteAllText(Path.Combine(_root, "smoke-progress.txt"), stage); }
        try {
            if (_prepareBrowsers) {
                Stage("prepare-empty-views"); var preparationStarted = System.Diagnostics.Stopwatch.GetTimestamp(); await PrepareEmptyViewsAsync().WaitAsync(TimeSpan.FromSeconds(45));
                results["preparationMilliseconds"] = System.Diagnostics.Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds;
                var emptyViewsPrepared = _views.Count == Launchers.All.Length && _views.Values.All(v => v.CoreWebView2?.Source == "about:blank");
                results["emptyViewsPreparedWithoutWebsiteNavigation"] = emptyViewsPrepared;
                if (!emptyViewsPrepared) throw new InvalidOperationException("Browser preparation navigated a website or left a controller unprepared");
                var restoredSettings=_views.All(pair => Math.Abs(pair.Value.ZoomFactor-GetSetting(pair.Key).Zoom)<.001 && pair.Value.CoreWebView2.Settings.UserAgent.Contains("Mobile")==GetSetting(pair.Key).MobileMode);
                results["perAppZoomAndIdentityRestoredIntoNewControllers"] = restoredSettings;
                results["initialControllerPreferences"] = _views.Select(pair=>new { app=pair.Key, zoom=pair.Value.ZoomFactor, mobile=GetSetting(pair.Key).MobileMode }).ToArray();
                if(!restoredSettings) throw new InvalidOperationException("Fresh browser controllers did not restore each app's saved zoom/identity");
            } else results["preparation"] = "Disabled for cold-controller comparison";
            Stage("first-launcher");
            var a = Launchers.All[0]; await ActivateAsync(a).WaitAsync(TimeSpan.FromSeconds(15)); await WaitDocumentAsync(_views[a.Id]);
            var toolbar320=_panel.CaptureToolbar(Path.Combine(_root,"toolbar-320.png"),320);
            var toolbar500=_panel.CaptureToolbar(Path.Combine(_root,"toolbar-500.png"),500);
            results["toolbarRender"] = new { at320=toolbar320,at500=toolbar500 };
            using(var toolbarDoc=JsonDocument.Parse(JsonSerializer.Serialize(toolbar320))) {
                var t=toolbarDoc.RootElement; if(!t.GetProperty("compact").GetBoolean() || t.GetProperty("toolbarWidth").GetDouble()>312 || !t.GetProperty("visibleControls").GetBoolean() || !t.GetProperty("noOverflow").GetBoolean() || t.GetProperty("toolbarHeight").GetDouble()>36) throw new InvalidOperationException("Compact Edgebar controls overflow or exceed the 36 DIP height limit");
            }
            _views[a.Id].CoreWebView2.IsMuted=true;
            if(GetSetting(a.Id).MobileMode) { await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render); ScheduleActiveMobileViewportRefresh(); await Task.Delay(350); }
            Stage("popup-and-audio");
            var instance = JsonSerializer.Deserialize<string>(await EvalAsync(_views[a.Id], "window.instanceId"));
            await EvalAsync(_views[a.Id],"window.fixtureAudioReady='pending';document.querySelector('#audio').play().then(()=>window.fixtureAudioReady='started',()=>window.fixtureAudioReady='failed');true");
            var audioStarted=(await WaitForValueAsync(_views[a.Id],"window.fixtureAudioReady","started",5000)) ? "true" : "false";
            var audioClockStarted=await WaitForValueAsync(_views[a.Id],"String(document.querySelector('#audio').currentTime>0)","true",5000);
            results["audioClockStartedBeforePopup"]=audioClockStarted;
            if(!audioClockStarted) throw new InvalidOperationException("Fixture audio clock did not start before popup checks");
            await EvalAsync(_views[a.Id], "window.addEventListener('message',e=>{if(typeof e.data==='string')window.fixturePopup=e.data});window.fixtureProxy=window.open('/popup','login-popup');localStorage.setItem('sidebar-fixture-marker','shared-account-a');document.querySelector('#input').focus();document.querySelector('#input').value='retained-input';window.scrollTo(0,700);true");
            var popupPassed = await WaitForValueAsync(_views[a.Id], "window.fixturePopup", "fixture-popup-ok", 5000);
            var popupCloseBridgeInjected = await EvalAsync(_views[a.Id], "window.fixtureCloseBridge===true") == "true";
            results["popupWindowCloseBridgeInjected"] = popupCloseBridgeInjected;
            var popupProxyClosed = await WaitForValueAsync(_views[a.Id], "String(window.fixtureProxy && window.fixtureProxy.closed)", "true", 3000);
            results["popupWindowProxyReportsClosed"] = popupProxyClosed;
            var blankPopupProxy = await EvalAsync(_views[a.Id], "(()=>{window.fixturePopup='';window.fixtureBlankProxy=window.open('about:blank','blank-auth');if(window.fixtureBlankProxy)window.fixtureBlankProxy.location.href='/popup';return !!window.fixtureBlankProxy})()");
            var blankPopupPassed = blankPopupProxy == "true" && await WaitForValueAsync(_views[a.Id], "window.fixturePopup", "fixture-popup-ok", 5000);
            var blankProxyClosed = blankPopupProxy == "true" && await WaitForValueAsync(_views[a.Id], "String(window.fixtureBlankProxy && window.fixtureBlankProxy.closed)", "true", 3000);
            results["blankPopupWindowProxyReportsClosed"] = blankProxyClosed;
            await EvalAsync(_views[a.Id],"window.fixturePopup='';window.open('/auth-start','auth-chain');true");
            var postRedirectPopupPassed = await WaitForValueAsync(_views[a.Id],"window.fixturePopup","fixture-auth-complete",10000);
            results["crossHostPopupPost307CallbackPassed"] = postRedirectPopupPassed; results["panelVisibleAfterPopupCloses"]=_panel.IsVisible && _state.IsVisible;
            if(!(bool)results["panelVisibleAfterPopupCloses"]!) throw new InvalidOperationException("Browser popup close hid its hosting panel");
            if(!postRedirectPopupPassed || _externalLaunchRequests!=0) throw new InvalidOperationException("Popup POST/redirect/callback chain escaped or lost its opener/body");
            var audioTime = await EvalAsync(_views[a.Id], "document.querySelector('#audio').currentTime");
            var visibleAudioTimer=System.Diagnostics.Stopwatch.StartNew(); await Task.Delay(2000); visibleAudioTimer.Stop();
            var audioAfterPopupClose = await EvalAsync(_views[a.Id], "document.querySelector('#audio').currentTime");
            var audioContinuesAfterPopupClose = JsonDocument.Parse(audioAfterPopupClose).RootElement.GetDouble() > JsonDocument.Parse(audioTime).RootElement.GetDouble();
            var visibleAudioDelta=JsonDocument.Parse(audioAfterPopupClose).RootElement.GetDouble()-JsonDocument.Parse(audioTime).RootElement.GetDouble();
            var visibleAudioProgress=visibleAudioDelta>=visibleAudioTimer.Elapsed.TotalSeconds*.5;
            results["audioAdvancesAfterPopupCloseWhilePanelVisible"] = audioContinuesAfterPopupClose;
            results["audioVisibleProgress"] = new { elapsedMs=visibleAudioTimer.Elapsed.TotalMilliseconds,deltaSeconds=visibleAudioDelta,hostMuted=_views[a.Id].CoreWebView2.IsMuted,elementUnmuted=await EvalAsync(_views[a.Id],"!document.querySelector('#audio').muted") };
            Stage("account-a-sharing");
            var hiddenAudioTimer=System.Diagnostics.Stopwatch.StartNew(); ClosePanel(); await ActivateAsync(Launchers.All[1]); await WaitDocumentAsync(_views[Launchers.All[1].Id]);
            await Task.Delay(2000);
            var audioContinued = await EvalAsync(_views[a.Id], "document.querySelector('#audio').currentTime");
            hiddenAudioTimer.Stop();
            var shared = await EvalAsync(_views[Launchers.All[1].Id], "localStorage.getItem('sidebar-fixture-marker')");
            Stage("account-b-isolation");
            await ActivateAsync(Launchers.All[2]); await WaitDocumentAsync(_views[Launchers.All[2].Id]);
            var isolated = await EvalAsync(_views[Launchers.All[2].Id], "localStorage.getItem('sidebar-fixture-marker')");
            Stage("retained-state"); var warmStart = System.Diagnostics.Stopwatch.GetTimestamp(); await ActivateAsync(a); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); results["warmActivationMilliseconds"] = System.Diagnostics.Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds;
            var restored = await EvalAsync(_views[a.Id], "JSON.stringify({instance:window.instanceId,input:document.querySelector('#input').value,scroll:Math.round(window.scrollY)})");
            var view = JsonDocument.Parse(JsonSerializer.Deserialize<string>(restored)!);
            var instanceKept = view.RootElement.GetProperty("instance").GetString() == instance;
            var inputKept = view.RootElement.GetProperty("input").GetString() == "retained-input";
            var scrollKept = view.RootElement.GetProperty("scroll").GetInt32() >= 650;
            var profileShared = JsonSerializer.Deserialize<string?>(shared) == "shared-account-a";
            var profileIsolated = JsonSerializer.Deserialize<string?>(isolated) != "shared-account-a";
            var hiddenTime = JsonDocument.Parse(audioContinued).RootElement.GetDouble(); var beforeHidden = JsonDocument.Parse(audioAfterPopupClose).RootElement.GetDouble();
            var audioAdvancedWhileHidden = hiddenTime > beforeHidden;
            var hiddenAudioDelta=hiddenTime-beforeHidden;
            var hiddenAudioProgress=hiddenAudioDelta>=hiddenAudioTimer.Elapsed.TotalSeconds*.5;
            results["authWindowCloseBridgeMessages"] = _authCloseBridgeMessages;
            var popupCloseBridgeDelivered = _authCloseBridgeMessages >= 3;
            results["nativePopupCloseEvents"] = _nativePopupCloseEvents;
            results["panelWindowCloseAttempts"] = _panel.CloseAttemptCount;
            var hostCloseCountersStayedZero = _nativePopupCloseEvents == 0 && _panel.CloseAttemptCount == 0;
            results["audioBeforeHidden"]=beforeHidden; results["audioAfterHidden"]=hiddenTime; results["audioHiddenProgress"] = new { elapsedMs=hiddenAudioTimer.Elapsed.TotalMilliseconds,deltaSeconds=hiddenAudioDelta,ratio=hiddenAudioDelta/hiddenAudioTimer.Elapsed.TotalSeconds,passes=hiddenAudioProgress,hostMuted=_views[a.Id].CoreWebView2.IsMuted,elementUnmuted=await EvalAsync(_views[a.Id],"!document.querySelector('#audio').muted") }; results["audioState"]=await EvalAsync(_views[a.Id],"JSON.stringify({paused:document.querySelector('#audio').paused,ready:document.querySelector('#audio').readyState,error:document.querySelector('#audio').error?.code})");
            results["retainedState"] = view.RootElement.Clone();
            results["checks"] = new { instanceKept, inputKept, scrollKept, profileShared, profileIsolated, popupPassed, blankPopupPassed, popupCloseBridgeInjected, popupCloseBridgeDelivered, popupProxyClosed, blankProxyClosed, hostCloseCountersStayedZero, audioStarted = JsonSerializer.Deserialize<bool>(audioStarted), audioContinuesAfterPopupClose, visibleAudioProgress, audioAdvancedWhileHidden, hiddenAudioProgress };
            if (!(instanceKept && inputKept && scrollKept && profileShared && profileIsolated && popupPassed && blankPopupPassed && popupCloseBridgeInjected && popupCloseBridgeDelivered && popupProxyClosed && blankProxyClosed && hostCloseCountersStayedZero && JsonSerializer.Deserialize<bool>(audioStarted) && audioContinuesAfterPopupClose && visibleAudioProgress && audioAdvancedWhileHidden && hiddenAudioProgress)) throw new InvalidOperationException("fixture browser assertion failed");
            Stage("zoom-burst"); var zoomStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var z = 0; z < 10; z++) OnToolbar("zoom+");
            results["zoomBurstCommandMilliseconds"] = System.Diagnostics.Stopwatch.GetElapsedTime(zoomStarted).TotalMilliseconds;
            await Task.Delay(150);
            var zoomBurstPassed = Math.Abs(_views[a.Id].ZoomFactor - 2) < 0.001 && Math.Abs(GetSetting(a.Id).Zoom - 2) < 0.001;
            OnToolbar("zoom0"); await Task.Delay(100); await _settingsWriter.FlushAsync();
            var zoomResetPassed = Math.Abs(_views[a.Id].ZoomFactor - 1) < 0.001 && AppSettings.SafeLoad(Path.Combine(_root,"settings.json")).Launchers[a.Id].Zoom == 1;
            results["zoomBurstPassed"] = zoomBurstPassed; results["zoomResetPersisted"] = zoomResetPassed;
            if (!zoomBurstPassed || !zoomResetPassed) throw new InvalidOperationException("Zoom burst/reset did not apply and persist");
            Stage("per-app-zoom-and-identity");
            QueueZoom(a.Id,1.3); await Task.Delay(80); await ActivateAsync(Launchers.All[1]); QueueZoom(Launchers.All[1].Id,.8); await Task.Delay(80); await ActivateAsync(a);
            var independentZoom = Math.Abs(_views[a.Id].ZoomFactor-1.3)<.001 && Math.Abs(_views[Launchers.All[1].Id].ZoomFactor-.8)<.001;
            var nativeBoundsBeforeIdentity=new { Left,Top,Width,Height,PanelWidth=_panel.Width,PanelHeight=_panel.Height }; var reservationBeforeIdentity=_shell.AppBarSetPosCallCount;
            await ToggleIdentityAsync(a.Id,_views[a.Id]); await Task.Delay(100); await WaitDocumentAsync(_views[a.Id]);
            var identity = await EvalAsync(_views[a.Id],"JSON.stringify({agent:navigator.userAgent,mobile:navigator.userAgentData?.mobile,header:window.requestAgent,hint:window.requestMobile,viewport:innerWidth,screen:screen.width,platform:navigator.platform,scrollWidth:document.documentElement.scrollWidth,clientWidth:document.documentElement.clientWidth})");
            using var identityDoc=JsonDocument.Parse(JsonSerializer.Deserialize<string>(identity)!);
            var mobile=GetSetting(a.Id).MobileMode;
            var correctIdentity=identityDoc.RootElement.GetProperty("agent").GetString()!.Contains("Mobile")==mobile && identityDoc.RootElement.GetProperty("mobile").GetBoolean()==mobile && identityDoc.RootElement.GetProperty("header").GetString()!.Contains("Mobile")==mobile && identityDoc.RootElement.GetProperty("hint").GetString()==(mobile ? "?1" : "?0");
            results["identityToggleKeepsHostGeometry"]=nativeBoundsBeforeIdentity.Equals(new { Left,Top,Width,Height,PanelWidth=_panel.Width,PanelHeight=_panel.Height }) && _shell.AppBarSetPosCallCount==reservationBeforeIdentity;
            if(!(bool)results["identityToggleKeepsHostGeometry"]!) throw new InvalidOperationException("Identity toggle changed host geometry or reserved space");
            if(!GetSetting(a.Id).MobileMode) { await ToggleIdentityAsync(a.Id,_views[a.Id]); await Task.Delay(100); await WaitDocumentAsync(_views[a.Id]); }
            async Task<(JsonElement Metrics,long AppBarCallsAfterCommit)> ResizeMetricsAsync(double panelWidth)
            {
                _panel.Width=panelWidth; await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render); CommitPanelResize();
                var appBarCallsAfterCommit=_shell.AppBarSetPosCallCount;
                await Task.Delay(350);
                var raw=await EvalAsync(_views[a.Id],"JSON.stringify({inner:innerWidth,client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth})");
                return (JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)!).RootElement.Clone(),appBarCallsAfterCommit);
            }
            Stage("mobile-viewport-resize");
            var mobileViewportStart=_mobileViewportUpdates; var narrowResize=await ResizeMetricsAsync(360); var narrowMetrics=narrowResize.Metrics; var narrowUpdateCount=_mobileViewportUpdates;
            var narrowFits=narrowMetrics.GetProperty("inner").GetInt32()==_mobileViewports[a.Id].Width && narrowMetrics.GetProperty("client").GetInt32()==_mobileViewports[a.Id].Width && narrowMetrics.GetProperty("scroll").GetInt32()<=narrowMetrics.GetProperty("client").GetInt32();
            var wideResize=await ResizeMetricsAsync(720); var wideMetrics=wideResize.Metrics; var wideUpdateCount=_mobileViewportUpdates;
            await Task.Delay(350);
            var resizeLoopStopped=_mobileViewportUpdates==wideUpdateCount;
            var wideFits=wideMetrics.GetProperty("inner").GetInt32()==_mobileViewports[a.Id].Width && wideMetrics.GetProperty("client").GetInt32()==_mobileViewports[a.Id].Width && wideMetrics.GetProperty("scroll").GetInt32()<=wideMetrics.GetProperty("client").GetInt32();
            var noExtraAppBarWrites=_shell.AppBarSetPosCallCount==wideResize.AppBarCallsAfterCommit;
            results["mobileViewportResizeFitsAndSettles"] = new { narrowFits,wideFits,updates=_mobileViewportUpdates-mobileViewportStart,narrow=narrowMetrics,wide=wideMetrics,resizeLoopStopped,noExtraAppBarWrites };
            if(!(narrowFits && wideFits && resizeLoopStopped && noExtraAppBarWrites && _mobileViewportUpdates-mobileViewportStart==2)) throw new InvalidOperationException("Mobile viewport did not follow a committed panel resize cleanly");
            await _settingsWriter.FlushAsync(); var readBack=AppSettings.SafeLoad(Path.Combine(_root,"settings.json"));
            results["independentAppZoom"] = independentZoom;
            results["mobileDesktopIdentityMatchesRequestAndClientHints"] = correctIdentity; results["viewportMetrics"]=identityDoc.RootElement.Clone();
            results["zoomAndIdentityPersisted"] = readBack.Launchers[a.Id].MobileMode==GetSetting(a.Id).MobileMode && Math.Abs(readBack.Launchers[a.Id].Zoom-1.3)<.001 && Math.Abs(readBack.Launchers[Launchers.All[1].Id].Zoom-.8)<.001;
            if(!independentZoom || !correctIdentity || !(bool)results["zoomAndIdentityPersisted"]!) throw new InvalidOperationException("App zoom/identity independence or persistence failed");
            Stage("remaining-launchers");
            foreach (var l in Launchers.All.Where(x => x.Id != a.Id && x.Id != Launchers.All[1].Id && x.Id != Launchers.All[2].Id)) { await ActivateAsync(l); await WaitDocumentAsync(_views[l.Id]); }
            Stage("app-return-into-panel"); var calendarApp=Launchers.All[3]; await ActivateAsync(calendarApp,false);
            var oldCalendar=_views[calendarApp.Id]; var nativeWindowsBefore=System.Windows.Application.Current.Windows.Count;
            var calendarBefore=oldCalendar.CoreWebView2.Source;
            await EvalAsync(oldCalendar,"window.fixturePopup='';window.fixtureGoogleProxy=window.open('about:blank','calendar-signin');if(window.fixtureGoogleProxy)window.fixtureGoogleProxy.location.href='/google-auth-intent';true");
            var promotionDeadline=DateTime.UtcNow.AddSeconds(10);
            while(_promotedAuthViews==0 && DateTime.UtcNow<promotionDeadline) await Task.Delay(50);
            await WaitDocumentAsync(_views[calendarApp.Id]);
            var authHandoff=_promotedAuthViews==1 && !ReferenceEquals(_views[calendarApp.Id],oldCalendar) && !_panel.HasActiveAuth && _fixtureAppReturns==1 && calendarBefore.EndsWith("/calendar-b/",StringComparison.Ordinal) && !_navigationSessions[calendarApp.Id].AuthenticationActive;
            results["signedInAppPromotedIntoPrimaryPanelWithoutReload"]=authHandoff;
            results["noNativePopupWindowsCreated"]=System.Windows.Application.Current.Windows.Count==nativeWindowsBefore;
            await EvalAsync(_views[calendarApp.Id],"window.close();true"); await Task.Delay(150);
            results["promotedViewCannotCloseHostPanel"]=_panel.IsVisible && _views[calendarApp.Id].CoreWebView2 is not null;
            if(!(bool)results["promotedViewCannotCloseHostPanel"]!) throw new InvalidOperationException("Promoted browser closed its containing panel");
            if(!authHandoff || !(bool)results["noNativePopupWindowsCreated"]!) throw new InvalidOperationException("Auth return remained in a separate surface or was reloaded/reparented");
            OpenExternal(new Uri("https://outside.example.test/"));
            results["automaticExternalLaunchBlocked"]=_externalLaunchRequests==0 && _externalSuppressed>0;
            if(!(bool)results["automaticExternalLaunchBlocked"]!) throw new InvalidOperationException("An automatic outside navigation launched the browser");
            Stage("main-cross-host-redirect"); var mainB=_views[Launchers.All[2].Id]; mainB.CoreWebView2.Navigate(_fixtureBase+"main-redirect"); await Task.Delay(200); await WaitDocumentAsync(mainB);
            var mainRedirectPassed=new Uri(mainB.CoreWebView2.Source).Host=="localhost" && _externalLaunchRequests==0;
            results["mainCrossHostRedirectStaysEmbedded"] = mainRedirectPassed;
            if(!mainRedirectPassed) throw new InvalidOperationException("Main automatic cross-host redirect escaped to the external browser");
            Stage("hidden-renderer-recovery");
            var recoveryLauncher = Launchers.All.Single(x => x.Id == "soundcloud"); ClosePanel(false);
            try { await _views[recoveryLauncher.Id].CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}").WaitAsync(TimeSpan.FromSeconds(2)); } catch(Exception crashError) { results["crashInvocationException"]=crashError.GetType().Name+": "+crashError.Message; }
            var failureDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!_initializationErrors.ContainsKey(recoveryLauncher.Id) && DateTime.UtcNow < failureDeadline) await Task.Delay(50);
            results["hiddenCrashRegistered"]=_initializationErrors.ContainsKey(recoveryLauncher.Id); await ActivateAsync(recoveryLauncher, false); var hiddenFailureSurfaced = _panel.HasFailure; results["hiddenFailureSurfaced"]=hiddenFailureSurfaced;
            await RetryAsync(recoveryLauncher); await WaitDocumentAsync(_views[recoveryLauncher.Id]);
            var hiddenFailureRecovered = hiddenFailureSurfaced && !_panel.HasFailure && !_initializationErrors.ContainsKey(recoveryLauncher.Id);
            results["hiddenRendererFailureSurfacedAndRecovered"] = hiddenFailureRecovered; results["recoveryHasFailure"]=_panel.HasFailure; results["recoveryHasStoredError"]=_initializationErrors.ContainsKey(recoveryLauncher.Id);
            if (!hiddenFailureRecovered) throw new InvalidOperationException("Hidden renderer failure did not surface a working Retry");
            await RunUnloadRegressionChecksAsync(results);
            if (!_browserOnly) {
            Stage("native-reservation");
            var baseArea = _workAreaBefore ?? RuntimeProbe.CaptureWorkArea(); var dockArea = RuntimeProbe.CaptureWorkArea();
            var initialMode = GetSetting(a.Id).Mode; GetSetting(a.Id).Mode = PanelMode.Split; Reserve(_settings.DockWidth, GetPanelWidth(GetSetting(a.Id)));
            var splitArea = RuntimeProbe.CaptureWorkArea(); var dockReserved = dockArea.Width < baseArea.Width; var splitReserved = splitArea.Width < dockArea.Width; GetSetting(a.Id).Mode = initialMode;
            if (!dockReserved || !splitReserved) throw new InvalidOperationException("Work-area reservation geometry did not change as expected");
            results["workAreas"] = new { before = baseArea, dock = dockArea, split = splitArea };
            await Task.Delay(4000);
            try { results["processMetrics"] = await RuntimeProbe.MeasureAsync(await _environment, 5000); } catch (Exception metricError) { results["metricsStatus"] = metricError.GetType().Name; }
            HideUtility(); var releasedArea = RuntimeProbe.CaptureWorkArea(); var workAreaRestored = releasedArea == baseArea;
            results["workAreaRestored"] = workAreaRestored;
            if (!workAreaRestored) throw new InvalidOperationException("Work area was not restored after hide");
            ToggleVisible(); var trayRestoreReserved = RuntimeProbe.CaptureWorkArea().Width == dockArea.Width; HideUtility();
            results["trayShowAfterHide"] = trayRestoreReserved;
            if (!trayRestoreReserved || RuntimeProbe.CaptureWorkArea() != baseArea) throw new InvalidOperationException("Tray show/hide reservation regression");
            } else { results["nativeReservationChecks"] = "Skipped: browser-only fixture avoids changing the desktop work area while another sidebar is running."; }
            results["status"] = "completed";
        }
        catch (Exception e) { results["status"] = "failed"; results["error"] = e.GetType().Name; results["diagnosticMessage"] = e.Message; results["diagnosticStack"] = e.StackTrace; try { HideUtility(); results["workAreaRestored"] = RuntimeProbe.CaptureWorkArea() == _workAreaBefore; } catch { } }
        results["interactionMetrics"] = _interactionMetrics.ToArray(); results["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds; await File.WriteAllTextAsync(Path.Combine(_root, "smoke-evidence.json"), JsonSerializer.Serialize<Dictionary<string, object?>>(results, new JsonSerializerOptions { WriteIndented = true })); Quit(results["status"]?.ToString() == "completed" ? 0 : 1);
    }
    private async Task RunLayoutRegressionAsync()
    {
        var results=new Dictionary<string,object?> { ["mode"]="native-layout-regression" };
        try {
            var a=Launchers.All[0]; var b=Launchers.All[1];
            GetSetting(a.Id).Mode=PanelMode.Split; GetSetting(a.Id).Width=430;
            GetSetting(b.Id).Mode=PanelMode.Overlay; GetSetting(b.Id).Width=610;
            await ActivateAsync(a); await Task.Delay(350);
            var area=RuntimeProbe.CaptureWorkArea(); var calls=_shell.AppBarSetPosCallCount; var width=_panel.Width;
            for(var i=0;i<40;i++) { await ActivateAsync(i%2==0 ? b : a,false); await Dispatcher.Yield(DispatcherPriority.Background); }
            await Task.Delay(200);
            results["switchCount"]=40; results["appBarSetPosDuringSwitches"]=_shell.AppBarSetPosCallCount-calls;
            results["desktopWorkAreaUnchanged"]=RuntimeProbe.CaptureWorkArea()==area;
            results["panelModeAndWidthUnchanged"]=_openMode==PanelMode.Split && _panel.Width==width;
            results["differentSavedModesAndWidthsPreserved"]=GetSetting(a.Id).Mode==PanelMode.Split && GetSetting(b.Id).Mode==PanelMode.Overlay && GetSetting(a.Id).Width==430 && GetSetting(b.Id).Width==610;
            if(_shell.AppBarSetPosCallCount!=calls || RuntimeProbe.CaptureWorkArea()!=area || _panel.Width!=width || _openMode!=PanelMode.Split) throw new InvalidOperationException("Tab switching changed the desktop reservation or panel geometry");
            await ActivateAsync(b,false); _panel.Width=610; await Dispatcher.Yield(DispatcherPriority.Background); CommitPanelResize();
            results["resizeMatchingSavedWidthChangesReservation"]=Math.Abs(_openWidth-610)<1 && RuntimeProbe.CaptureWorkArea().Width<area.Width;
            if(!(bool)results["resizeMatchingSavedWidthChangesReservation"]!) throw new InvalidOperationException("Resize to another app's already saved width did not update reservation");
            _views[b.Id]=new WebView2(); OnToolbar("mode"); await Task.Delay(200);
            results["explicitModeButtonChangesReservation"]=_openMode==PanelMode.Overlay && RuntimeProbe.CaptureWorkArea().Width>area.Width;
            if(!(bool)results["explicitModeButtonChangesReservation"]!) throw new InvalidOperationException("Explicit mode toggle did not release panel reservation");
            ClosePanel(false); await ActivateAsync(b); results["freshOpeningUsesSavedPreference"]=_openMode==PanelMode.Overlay && Math.Abs(_panel.Width-610)<1;
            results["status"]="completed";
        } catch(Exception error) { results["status"]="failed"; results["error"]=error.Message; }
        finally { ClosePanel(false); _shell.Release(); }
        await File.WriteAllTextAsync(Path.Combine(_root,"layout-evidence.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
        Quit(results["status"]?.ToString()=="completed" ? 0 : 1);
    }
    private async Task RunTransitionRegressionAsync()
    {
        var results=new Dictionary<string,object?> { ["mode"]=_transitionToolWindow ? "real-webview-transition-native-appbar-toolwindow" : _transitionNative ? "real-webview-transition-with-native-appbar" : "real-webview-transition-with-mocked-appbar",["startedUtc"]=DateTime.UtcNow };
        long ReservationCount() => _transitionNative ? _shell.AppBarSetPosCallCount : _mockReservationCount;
        var baseArea=_workAreaBefore ?? RuntimeProbe.CaptureWorkArea(); var sw=System.Diagnostics.Stopwatch.StartNew();
        var panelSizeEvents=0; var panelLocationEvents=0; var dockSizeEvents=0; var dockLocationEvents=0;
        _panel.SizeChanged+=(_,_)=>panelSizeEvents++; _panel.LocationChanged+=(_,_)=>panelLocationEvents++;
        SizeChanged+=(_,_)=>dockSizeEvents++; LocationChanged+=(_,_)=>{ dockLocationEvents++; if(_transitionTest) RecordNativeBounds("dock",new System.Windows.Interop.WindowInteropHelper(this).Handle,0x0003); };
        WindowRectSnapshot? Bounds(Window window)
        {
            var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;
            return hwnd!=0 && NativeMethods.GetWindowRect(hwnd,out var rect) ? new WindowRectSnapshot(rect.Left,rect.Top,rect.Right,rect.Bottom) : null;
        }
        void Sample()
        {
            if(_transitionSamples.Count>=2500) return;
            var p=Bounds(_panel); var d=Bounds(this);
            var panelHwnd=new System.Windows.Interop.WindowInteropHelper(_panel).Handle; var dockHwnd=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var panelDpi=panelHwnd!=0 ? checked((int)NativeMethods.GetDpiForWindow(panelHwnd)) : 0; var dockDpi=dockHwnd!=0 ? checked((int)NativeMethods.GetDpiForWindow(dockHwnd)) : 0;
            var approvedLayout=_shell.GetLayout().DockBounds; var scale=dockDpi/96.0;
            var approvedDock=approvedLayout.IsEmpty || dockDpi<=0 ? null : new WindowRectSnapshot((int)Math.Round(approvedLayout.Left*scale),(int)Math.Round(approvedLayout.Top*scale),(int)Math.Round(approvedLayout.Right*scale),(int)Math.Round(approvedLayout.Bottom*scale));
            _transitionSamples.Add(new TransitionSample(sw.Elapsed.TotalMilliseconds,_transitionTraceStartTick!=0 && Environment.TickCount64>=_transitionTraceStartTick,_state.IsVisible,NativeMethods.IsWindowVisible(dockHwnd),_shell.IsReserved,_state.ActiveId,_openMode.ToString(),_openWidth,
                Finite(_panel.Width),_panel.ActualWidth,_panel.ActualHeight,panelDpi,dockDpi,p,d,approvedDock,RuntimeProbe.CaptureWorkArea(),_shell.AppBarSetPosCallCount,_mockReservationCount,_layoutApplicationCount,
                _panel.WindowPositionMessages,_panel.WindowMoveMessages,_panel.WindowSizeMessages,_transitionDockPosMessages,_transitionDockMoveMessages,_transitionDockSizeMessages));
        }
        var sampler=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(12) }; sampler.Tick+=(_,_)=>Sample();
        try {
            foreach(var l in Launchers.All) {
                var s=GetSetting(l.Id); s.Mode=l.Id.StartsWith("calendar",StringComparison.Ordinal) ? PanelMode.Overlay : PanelMode.Split;
                s.Width=l.Id switch { "gmail-a"=>500,"gmail-b"=>564,"soundcloud"=>605,_=>0 }; s.MobileMode=l.Id=="soundcloud";
                s.Zoom=l.Id switch { "gmail-a"=>.67,"soundcloud"=>.6,_=>1 };
            }
            Topmost=false; _panel.Topmost=false; sampler.Start();
            var first=Launchers.All[0]; var staleOpen=ActivateAsync(first,false); await Task.Delay(8); await ActivateAsync(first,true); await staleOpen.WaitAsync(TimeSpan.FromSeconds(15)); await Task.Delay(250); Sample();
            var canceledOpenStayedClosed=!_state.IsVisible && !_panel.IsVisible;
            results["canceledActivationDidNotReopenAfterSameIconClose"]=canceledOpenStayedClosed;

            foreach(var l in Launchers.All) { await ActivateAsync(l,false); await WaitDocumentAsync(_views[l.Id]); await Dispatcher.Yield(DispatcherPriority.Render); Sample(); }
            if(_state.IsVisible) ClosePanel(false); await Task.Delay(100);
            var viewRefs=_views.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            var navStarts=0; foreach(var view in _views.Values) view.CoreWebView2.NavigationStarting+=(_,_)=>navStarts++;
            var baselineNavStarts=navStarts; var initialLayouts=_layoutApplicationCount; var initialReservations=ReservationCount();
            var panelMessagesBefore=(_panel.WindowPositionMessages,_panel.WindowMoveMessages,_panel.WindowSizeMessages);
            var panelBoundsAtCheckpoints=new List<TransitionStep>();
            async Task OpenCloseAsync(Launcher l)
            {
                var beforeOpenLayouts=_layoutApplicationCount; var beforeOpenReservations=ReservationCount();
                await ActivateAsync(l,false); await Dispatcher.Yield(DispatcherPriority.Render); Sample();
                var openWidth=_openWidth; var openMode=_openMode;
                await Task.Delay(24); var beforeCloseReservations=ReservationCount(); await ActivateAsync(l,true); await Dispatcher.Yield(DispatcherPriority.Render); Sample();
                panelBoundsAtCheckpoints.Add(new TransitionStep(l.Id,openWidth,openMode.ToString(),_layoutApplicationCount-beforeOpenLayouts,beforeCloseReservations-beforeOpenReservations,ReservationCount()-beforeCloseReservations,Bounds(_panel)));
                await Task.Delay(24);
            }
            _transitionTraceStartTick=Environment.TickCount64;
            if(_transitionToolWindow) {
                // Focused native A/B: open and close the same Split Gmail panel twice.
                await OpenCloseAsync(Launchers.All.Single(x=>x.Id=="gmail-a"));
                await OpenCloseAsync(Launchers.All.Single(x=>x.Id=="gmail-a"));
            } else {
                foreach(var id in new[]{"gmail-a","gmail-a","gmail-b","soundcloud","gmail-a","soundcloud","gmail-b"}) await OpenCloseAsync(Launchers.All.Single(x=>x.Id==id));
            }

            var switchStartReservations=ReservationCount();
            long switchAfterOpenReservations=switchStartReservations, switchBeforeCloseReservations=switchStartReservations, switchCloseReservationDelta=0;
            double switchStartWidth=0; var switchStartMode=PanelMode.Split;
            var noExtraReservationForOpenSwitches=true; var openGeometryPreserved=true;
            if(!_transitionToolWindow) {
                await ActivateAsync(Launchers.All[0],false); switchAfterOpenReservations=ReservationCount(); switchStartWidth=_openWidth; switchStartMode=_openMode;
                foreach(var index in new[]{2,4,1,3,0}) { await ActivateAsync(Launchers.All[index],false); await Dispatcher.Yield(DispatcherPriority.Render); Sample(); if(_panel.IsVisible) await WaitDocumentAsync(_views[Launchers.All[index].Id]); }
                switchBeforeCloseReservations=ReservationCount();
                noExtraReservationForOpenSwitches=switchAfterOpenReservations-switchStartReservations==1 && switchBeforeCloseReservations-switchAfterOpenReservations==0;
                openGeometryPreserved=Math.Abs(_openWidth-switchStartWidth)<.5 && _openMode==switchStartMode && Math.Abs(_panel.ActualWidth-switchStartWidth)<2;
                await ActivateAsync(Launchers.All[0],true); await Dispatcher.Yield(DispatcherPriority.Render); Sample();
                switchCloseReservationDelta=ReservationCount()-switchBeforeCloseReservations;
            }
            var expectedPerStepDelta=_transitionNative ? 0 : 1;
            var edgeReservationsMatch=panelBoundsAtCheckpoints.All(x=>x.Mode==PanelMode.Split.ToString() ? x.OpenReservationDelta==1 && x.CloseReservationDelta==1 : x.OpenReservationDelta==expectedPerStepDelta && x.CloseReservationDelta==expectedPerStepDelta)
                && (_transitionToolWindow || switchAfterOpenReservations-switchStartReservations==1 && switchBeforeCloseReservations-switchAfterOpenReservations==0 && switchCloseReservationDelta==1);
            if(!_transitionToolWindow) { await OpenCloseAsync(Launchers.All[1]); await OpenCloseAsync(Launchers.All[3]); }
            await Task.Delay(300); Sample();

            var geometryRows=_transitionSamples.Where(x=>x.Visible).ToArray();
            var maxVisibleWidth=geometryRows.Length==0 ? 0d : geometryRows.Max(x=>x.PanelActualWidth);
            var maxWidthError=geometryRows.Length==0 ? double.PositiveInfinity : geometryRows.Max(x=>Math.Abs(x.PanelActualWidth-x.OpenWidth));
            var noOversizeFrame=geometryRows.Length>0 && maxWidthError<=2;
            bool SampleBoundsMatch(TransitionSample x)
            {
                if(x.PanelRect is null || x.DockRect is null || x.PanelDpi<=0 || x.DockDpi<=0) return false;
                var expectedPanel=(int)Math.Round(x.OpenWidth*x.PanelDpi/96.0); var expectedDock=(int)Math.Round(_settings.DockWidth*x.DockDpi/96.0);
                return Math.Abs(x.PanelRect.Width-expectedPanel)<=2 && Math.Abs(x.DockRect.Width-expectedDock)<=2 && x.PanelRect.Left==x.DockRect.Right && x.PanelRect.Top==x.DockRect.Top && x.PanelRect.Bottom==x.DockRect.Bottom;
            }
            bool EventBoundsMatch(NativeBoundsEvent x)
            {
                if(x.Dpi<=0 || x.Window=="panel" && !x.Visible) return true;
                var expected=(int)Math.Round((x.Window=="panel" ? x.OpenWidth : _settings.DockWidth)*x.Dpi/96.0);
                if(Math.Abs(x.Rect.Width-expected)>2) return false;
                // During WM_WINDOWPOSCHANGING the companion HWND can be between its own AppBar moves.
                // Compare the event's HWND with the authoritative layout coordinates instead of that transient rect.
                var expectedLeft=x.Window=="panel" ? x.ShellPanelLeft : x.ShellDockLeft;
                var expectedTop=x.WorkArea.Top;
                if(expectedLeft is null || Math.Abs(x.Rect.Left-(int)Math.Round(expectedLeft.Value*x.Dpi/96.0))>2 || x.Rect.Top!=expectedTop) return false;
                if(x.Proposed is { } pending) {
                    if((pending.Flags&0x0002)==0 && (Math.Abs(pending.X-(int)Math.Round(expectedLeft.Value*x.Dpi/96.0))>2 || pending.Y!=expectedTop)) return false;
                    if((pending.Flags&0x0001)==0 && (Math.Abs(pending.Width-expected)>2 || Math.Abs(pending.Height-(x.WorkArea.Bottom-x.WorkArea.Top))>2)) return false;
                }
                return true;
            }
            var nativeSampleGeometryMatches=geometryRows.Length>0 && geometryRows.All(SampleBoundsMatch);
            var dockRows=_transitionSamples.Where(x=>x.TraceArmed && x.DockRegistered && x.DockWindowVisible).ToArray();
            var closedDockRows=dockRows.Where(x=>!x.Visible).ToArray();
            bool DockSampleMatches(TransitionSample x) => x.DockRect is not null && x.ApprovedDockRect is not null && x.DockDpi>0 &&
                Math.Abs(x.DockRect.Left-x.ApprovedDockRect.Left)<=2 && Math.Abs(x.DockRect.Top-x.ApprovedDockRect.Top)<=2 && Math.Abs(x.DockRect.Width-x.ApprovedDockRect.Width)<=2 && Math.Abs(x.DockRect.Height-x.ApprovedDockRect.Height)<=2;
            var nativeDockSampleGeometryMatches=dockRows.Length>0 && dockRows.All(DockSampleMatches);
            var closedDockGeometryMatches=closedDockRows.Length>0 && closedDockRows.All(DockSampleMatches);
            var checkedNativeEvents=_nativeBoundsEvents.Where(x=>x.AtTick>=_transitionTraceStartTick && (x.Window=="dock" || x.Visible)).ToArray();
            var nativeEventGeometryMatches=checkedNativeEvents.Length>0 && checkedNativeEvents.All(EventBoundsMatch);
            var controllersStable=viewRefs.All(pair=>_views.TryGetValue(pair.Key,out var view) && ReferenceEquals(pair.Value,view));
            var finalSourcesUnchanged=viewRefs.All(pair=>_views[pair.Key].CoreWebView2.Source==pair.Value.CoreWebView2.Source);
            var noNavigationOnSwitch=navStarts==baselineNavStarts;
            var workAreaUnchanged=!_transitionNative && RuntimeProbe.CaptureWorkArea()==baseArea;
            var expectedReservationDelta=(_transitionNative ? panelBoundsAtCheckpoints.Count(x=>x.Mode==PanelMode.Split.ToString())*2 : panelBoundsAtCheckpoints.Count()*2)+(_transitionToolWindow ? 0 : 2);
            var exactReservationTransitions=edgeReservationsMatch && ReservationCount()-initialReservations==expectedReservationDelta;
            results["controllerInstancesStableAcrossToggles"]=controllersStable;
            results["sourcesUnchangedAfterWarmup"]=finalSourcesUnchanged;
            results["noNavigationDuringTransitionSequences"]=noNavigationOnSwitch;
            results["openAppSwitchDoesNotRenegotiateReservation"]=noExtraReservationForOpenSwitches;
            results["switchReservationDeltas"]=new { open=switchAfterOpenReservations-switchStartReservations,duringSwitches=switchBeforeCloseReservations-switchAfterOpenReservations,close=switchCloseReservationDelta };
            results["openSwitchPreservesPanelModeAndWidth"]=openGeometryPreserved;
            results[_transitionNative ? "nativeAppBarSetPosTransitions" : "expectedMockReservationRequestsOnly"]=new { passed=exactReservationTransitions,expected=expectedReservationDelta,actual=ReservationCount()-initialReservations,edgeReservationsMatch,switchOpen=switchAfterOpenReservations-switchStartReservations,switches=switchBeforeCloseReservations-switchAfterOpenReservations,switchClose=switchCloseReservationDelta };
            results["noVisibleOversizePanelFrame"] = new { passed=noOversizeFrame,maxVisibleWidth,maxWidthError,visibleSamples=geometryRows.Length };
            results["nativeGeometryAssertions"]=new { passed=nativeSampleGeometryMatches && nativeDockSampleGeometryMatches && closedDockGeometryMatches && nativeEventGeometryMatches,allVisiblePanelSamplesMatch=nativeSampleGeometryMatches,allRegisteredDockSamplesMatch=nativeDockSampleGeometryMatches,closedDockSamplesMatch=closedDockGeometryMatches,allArmedPositionEventsMatch=nativeEventGeometryMatches,visiblePositionEvents=checkedNativeEvents.Length,dockSamples=dockRows.Length,closedDockSamples=closedDockRows.Length,visibleEventsBeforeTraceArm=_nativeBoundsEvents.Count(x=>x.Visible && x.AtTick<_transitionTraceStartTick),panelDpi=geometryRows.Select(x=>x.PanelDpi).Distinct().ToArray(),dockDpi=geometryRows.Select(x=>x.DockDpi).Distinct().ToArray() };
            results["workAreaUnchanged"]=workAreaUnchanged;
            results["hostMovementAndLayoutEvents"]=new { panelSizeEvents,panelLocationEvents,dockSizeEvents,dockLocationEvents,layoutApplications=_layoutApplicationCount-initialLayouts,
                panelPositionMessages=_panel.WindowPositionMessages-panelMessagesBefore.WindowPositionMessages,panelMoveMessages=_panel.WindowMoveMessages-panelMessagesBefore.WindowMoveMessages,panelSizeMessages=_panel.WindowSizeMessages-panelMessagesBefore.WindowSizeMessages,
                dockPositionMessages=_transitionDockPosMessages,dockMoveMessages=_transitionDockMoveMessages,dockSizeMessages=_transitionDockSizeMessages };
            results["steps"]=panelBoundsAtCheckpoints; results["contentMetrics"]=new {
                gmailA=await EvalAsync(_views["gmail-a"],"JSON.stringify({inner:innerWidth,screen:screen.width,scroll:document.documentElement.scrollWidth,client:document.documentElement.clientWidth})"),
                gmailB=await EvalAsync(_views["gmail-b"],"JSON.stringify({inner:innerWidth,screen:screen.width,scroll:document.documentElement.scrollWidth,client:document.documentElement.clientWidth})"),
                soundcloud=await EvalAsync(_views["soundcloud"],"JSON.stringify({inner:innerWidth,screen:screen.width,scroll:document.documentElement.scrollWidth,client:document.documentElement.clientWidth})") };
            results["appBarMode"]=_transitionNative ? "native AppBar; isolated reservation released before completion" : "mocked to avoid changing the desktop reservation";
            results["status"]=canceledOpenStayedClosed && noExtraReservationForOpenSwitches && openGeometryPreserved && exactReservationTransitions && noOversizeFrame && nativeSampleGeometryMatches && nativeDockSampleGeometryMatches && closedDockGeometryMatches && nativeEventGeometryMatches && controllersStable && finalSourcesUnchanged && noNavigationOnSwitch && (_transitionNative || workAreaUnchanged) ? "completed" : "failed";
            if(results["status"]?.ToString()!="completed") throw new InvalidOperationException("Transition geometry, lifecycle or reservation assertion failed");
        } catch(Exception error) { results["status"]="failed"; results["error"]=error.GetType().Name; results["message"]=error.Message; }
        finally { sampler.Stop(); if(_state.IsVisible) ClosePanel(false); _shell.Release(); _transitionSource?.RemoveHook(TransitionWindowProc); }
        results["samples"]=_transitionSamples; results["mockReservations"]=_mockReservations;
        if(_transitionNative) await Task.Delay(500);
        var releasedArea=RuntimeProbe.CaptureWorkArea(); var workAreaRestored=releasedArea==baseArea;
        results["workAreaBeforeInitialReservation"]=baseArea; results["workAreaAfterRelease"]=releasedArea; results["finalWorkAreaRestored"]=workAreaRestored; results["elapsedMs"]=sw.Elapsed.TotalMilliseconds;
        results["nativeBoundsEvents"]=_nativeBoundsEvents;
        results["layoutApplyEvents"]=_layoutApplyEvents; results["panelWindowStyleChanges"]=_transitionWindowStyles;
        if(_transitionNative) results["status"]=results["status"]?.ToString()=="completed" && workAreaRestored ? "completed" : "failed";
        await File.WriteAllTextAsync(Path.Combine(_root,"transition-evidence.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
        Quit(results["status"]?.ToString()=="completed" ? 0 : 1);
    }
    private static async Task WaitDocumentAsync(WebView2 view)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { try { if (await EvalAsync(view, "document.readyState==='complete' && !!document.querySelector('#input')") == "true") return; } catch { } await Task.Delay(100); }
        throw new TimeoutException("Fixture document did not load");
    }
    private static async Task<bool> WaitForValueAsync(WebView2 view, string expression, string expected, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end) { if (JsonSerializer.Deserialize<string?>(await EvalAsync(view, expression)) == expected) return true; await Task.Delay(100); }
        return false;
    }
    private static async Task<string> EvalAsync(WebView2 view, string script) { if(view.CoreWebView2 is not {} core) throw new InvalidOperationException($"Fixture browser detached; loaded={view.IsLoaded}; visible={view.IsVisible}; parent={view.Parent?.GetType().Name}; windowLoaded={Window.GetWindow(view)?.IsLoaded}; windowVisible={Window.GetWindow(view)?.IsVisible}"); return await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(5)); }
    private async void Quit(int exitCode = 0) { if (_quitting) return; _quitting = true; _brandingTimer.Stop(); _state.Hide(); _tray.Visible = false; await DisposeAsync(); Close(); System.Windows.Application.Current.Shutdown(exitCode); }
    private async Task DisposeAsync()
    {
        _shell.Release(); _shell.Dispose(); _tray.Dispose(); _fixtureServer?.Stop(); _panel.ClosePopups();
        _focusWatch.Stop(); _hotkey.Dispose();
        foreach (var v in _views.Values.Concat(_retiredViews.Select(x=>x.View)).Distinct()) { try { v.Dispose(); } catch { } }
        foreach (var popup in _ownedPopups) { try { popup.View.Dispose(); } catch { } }
        _zoomTimer.Stop(); _mobileViewportTimer.Stop(); _brandingTimer.Stop(); await _settingsWriter.FlushAsync();
        _diagnosticTimer.Stop(); QueueDiagnosticWrite(); await _diagnosticWrite;
    }
    internal void EmergencyCleanup() { _quitting = true; _brandingTimer.Stop(); _state.Hide(); try { _focusWatch.Stop(); _hotkey.Dispose(); _panel.ClosePopups(); _tray.Visible = false; _tray.Dispose(); _fixtureServer?.Stop(); _shell.Release(); _shell.Dispose(); } catch { } }
}

internal sealed class ShortcutDialog : Window
{
    public uint Modifiers { get; private set; }
    public uint VirtualKey { get; private set; }
    public ShortcutDialog()
    {
        Title = "Set sidebar shortcut"; Width = 380; Height = 140; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Content = new TextBlock { Text = "Press a key combination (for example Ctrl + Alt + Space). Press Escape to cancel.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center };
        PreviewKeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; return; }
            if (e.Key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
            var state = System.Windows.Input.Keyboard.Modifiers; uint mods = 0x4000;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Alt)) mods |= 0x0001;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Control)) mods |= 0x0002;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Shift)) mods |= 0x0004;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Windows)) mods |= 0x0008;
            if ((mods & 0x000F) == 0) return;
            Modifiers = mods; VirtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key); e.Handled = true; DialogResult = true;
        };
    }
}
