using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SidebarPrototype.Shell;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace SidebarPrototype;

internal sealed partial class HostWindow : Window
{
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly HostWindowOptions _options;
    private readonly DeferredSettingsWriter _settingsWriter;
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _mobileViewportTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<WebView2, (string Id, double Target)> _pendingZoom = [];
    private readonly ViewState _state = new();
    private readonly Dictionary<string, WebView2> _views = [];
    private readonly Dictionary<WebView2, string> _viewOwners = [];
    private readonly Dictionary<string, long> _appEpochs = [];
    private readonly HashSet<string> _explicitlyUnloadedApps = [];
    private readonly Dictionary<string, string> _initializationErrors = [];
    private readonly Dictionary<string, NavigationSession> _navigationSessions = [];
    private readonly Dictionary<string, string> _desktopAgents = [];
    private readonly Dictionary<string, (int Width, int Height)> _mobileViewports = [];
    private int _mobileViewportUpdates;
    private int _layoutApplicationCount;
    private int _externalLaunchRequests;
    private PanelMode _openMode;
    private double _openWidth;
    private readonly Dictionary<string, Task> _initializers = [];
    private readonly PanelWindow _panel;
    private readonly DesktopDockService _shell;
    private readonly NotifyIcon _tray;
    private readonly GlobalHotkey _hotkey;
    private readonly Task<CoreWebView2Environment> _environment;
    private readonly DispatcherTimer _diagnosticTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Task _diagnosticWrite = Task.CompletedTask;
    private readonly DispatcherTimer _focusWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };

#if !TEST_HARNESS
    // Only the test build starts a fixture server, so the release build never has a fixture address.
    private readonly string? _fixtureBase = null;
#endif

    public HostWindow(string root, AppSettings settings, HostWindowOptions options)
    {
        _root = root;
        _settings = settings;
        _options = options;
        RefreshConfiguredLaunchers();
        _settingsWriter = new(settings, Path.Combine(root, "settings.json"));
        _diagnosticTimer.Tick += (_, _) =>
        {
            _diagnosticTimer.Stop();
            QueueDiagnosticWrite();
        };
        _zoomTimer.Tick += (_, _) => ApplyPendingZoom();
        _mobileViewportTimer.Tick += async (_, _) =>
        {
            _mobileViewportTimer.Stop();
            await ApplyActiveMobileViewportAsync();
        };
        Title = AppBranding.TheUsuals;
        Width = settings.DockWidth;
        Height = 480;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.Black;
        Topmost = settings.Topmost && !_options.TransitionTest;
        ShowInTaskbar = _options.Fixture && !_options.BrowserOnly && !_options.TransitionTest;
        ShowActivated = !_options.BrowserOnly && !_options.LayoutOnly && !_options.TransitionTest;
        if (_options.TransitionNative)
            Opacity = 0;
        Content = _dock;
        _panel = new PanelWindow(this)
        {
            ShowInTaskbar = _options.Fixture && !_options.BrowserOnly && !_options.TransitionTest,
            ShowActivated = !_options.BrowserOnly && !_options.LayoutOnly && !_options.TransitionTest,
            Opacity = _options.TransitionNative ? 0 : 1,
        };
        _shell = new DesktopDockService();
        _shell.LayoutChanged += (_, _) => ApplyLayout();
        _shell.Attach(this);
        ApplyUtilityToolWindowStyle(this, "dock");
#if TEST_HARNESS
        if (_options.TransitionTest)
            InstallTransitionWindowHook();
#endif
        if (_options.TransitionNative)
            _shell.SetTopmost(false);
        else if (!_options.TransitionTest)
            _shell.SetTopmost(settings.Topmost);
        _panel.Topmost = settings.Topmost && !_options.TransitionTest;
        _hotkey = new GlobalHotkey(this);
        string? hotkeyError = null;
        var hotkeyOk =
            _options.TransitionTest
            || _hotkey.TryRegister(settings.ShortcutModifiers, settings.ShortcutVirtualKey, out hotkeyError);
        _hotkey.Pressed += (_, _) => OnGlobalHotkey();
        _environment = CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "browser-data"));
        BuildButtons();
        _focusWatch.Tick += (_, _) =>
        {
            if (
                _options.Smoke
                || _panel.HasActiveAuth
                || !_state.IsVisible
                || _state.ActiveId is not { } active
                || _openMode != PanelMode.Overlay
            )
                return;
            var hwnd = ForegroundWindowHelper.GetForegroundWindow();
            if (hwnd != 0 && !ForegroundWindowHelper.IsOwnedByCurrentProcess(hwnd))
                ClosePanel(false);
        };
        _focusWatch.Start();
        _panel.UserResizeCompleted += (_, _) => CommitPanelResize();
        _tray = new NotifyIcon
        {
            Text = "Sidebar Prototype",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = !_options.TransitionTest,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleVisible());
        menu.Items.Add("Keyboard shortcut…", null, (_, _) => ConfigureShortcut());
        menu.Items.Add("Quit sidebar", null, (_, _) => Quit());
        if (!hotkeyOk)
            menu.Items.Add("Shortcut unavailable: " + (hotkeyError ?? "registration failed"), null, (_, _) => { });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleVisible();
        InitializeBranding();
        SourceInitialized += (_, _) => ApplyLayout();
        Loaded += (_, _) =>
        {
#if TEST_HARNESS
            _workAreaBefore = RuntimeProbe.CaptureWorkArea();
#endif
            _panel.Owner = this;
            _panel.EnsureNativeWindowConfigured();
            Reserve(_settings.DockWidth, 0);
            if (_options.BrowserOnly || (_options.TransitionTest && !_options.TransitionNative))
                ApplyLayout();
        };
        Closing += (_, e) =>
        {
            if (!_quitting)
            {
                e.Cancel = true;
                HideUtility();
            }
        };
#if TEST_HARNESS
        var runsRegression = AttachTestHarness();
#else
        const bool runsRegression = false;
#endif
        if (!runsRegression && _options.PrepareBrowsers)
            Loaded += (_, _) =>
            {
                _ = Dispatcher.BeginInvoke(
                    new Action(async () => await PrepareEmptyViewsAsync()),
                    DispatcherPriority.ApplicationIdle
                );
            };
    }

    private bool _quitting;
    internal bool IsQuitting => _quitting;

    private nint _previousForeground;
    private string? _lastLauncherId;

    private void ClosePanel(bool restoreFocus = true)
    {
        _state.Hide();
        MarkActiveDockButton(null);
        _panel.Hide();
        if (IsVisible)
            Reserve(_settings.DockWidth, 0);
        if (restoreFocus && _previousForeground != 0)
            ForegroundWindowHelper.Restore(_previousForeground);
        _previousForeground = 0;
        _ = SaveAsync();
    }

    private void HideUtility()
    {
        ClosePanel();
        Hide();
        _shell.Release();
    }

    private void ToggleVisible()
    {
        if (IsVisible)
            HideUtility();
        else
        {
            Show();
            Reserve(_settings.DockWidth, 0);
            if (_options.BrowserOnly || (_options.TransitionTest && !_options.TransitionNative))
                ApplyLayout();
            Activate();
        }
    }

    private void OnGlobalHotkey()
    {
        if (
            _state.IsVisible
            && ForegroundWindowHelper.IsOwnedByCurrentProcess(ForegroundWindowHelper.GetForegroundWindow())
        )
            ClosePanel();
        else if (_lastLauncherId is { } id && ConfiguredLaunchers.FirstOrDefault(x => x.Id == id) is { } launcher)
            _ = ActivateAsync(launcher, false);
        else
            ToggleVisible();
    }

    private void ConfigureShortcut()
    {
        var dialog = new ShortcutDialog { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var oldMods = _settings.ShortcutModifiers;
        var oldKey = _settings.ShortcutVirtualKey;
        if (_hotkey.TryRegister(dialog.Modifiers, dialog.VirtualKey, out var error))
        {
            _settings.ShortcutModifiers = dialog.Modifiers;
            _settings.ShortcutVirtualKey = dialog.VirtualKey;
            _ = SaveAsync();
        }
        else
        {
            _hotkey.TryRegister(oldMods, oldKey, out _);
            System.Windows.MessageBox.Show(
                error ?? "Shortcut registration failed.",
                _brandName,
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
    }

    private double GetPanelWidth(LauncherSetting s)
    {
        var width = s.Width > 0 ? s.Width : PanelSizing.Initial(SystemParameters.PrimaryScreenWidth);
        return PanelSizing.Clamp(width, SystemParameters.PrimaryScreenWidth, _settings.DockWidth);
    }

    private LauncherSetting GetSetting(string id)
    {
        if (!_settings.Launchers.TryGetValue(id, out var s))
            _settings.Launchers[id] = s = new();
        return s;
    }

    private Task SaveAsync()
    {
        _settingsWriter.Schedule();
        return Task.CompletedTask;
    }

    private async void Quit(int exitCode = 0)
    {
        if (_quitting)
            return;
        _quitting = true;
        _brandingTimer.Stop();
        _state.Hide();
        _tray.Visible = false;
        await DisposeAsync();
        Close();
        System.Windows.Application.Current.Shutdown(exitCode);
    }

    private async Task DisposeAsync()
    {
        _shell.Release();
        _shell.Dispose();
        _tray.Dispose();
#if TEST_HARNESS
        _fixtureServer?.Stop();
#endif
        _panel.ClosePopups();
        _focusWatch.Stop();
        _hotkey.Dispose();
        foreach (var v in _views.Values.Concat(_retiredViews.Select(x => x.View)).Distinct())
        {
            try
            {
                v.Dispose();
            }
            catch { }
        }
        foreach (var popup in _ownedPopups)
        {
            try
            {
                popup.View.Dispose();
            }
            catch { }
        }
        _zoomTimer.Stop();
        _mobileViewportTimer.Stop();
        _brandingTimer.Stop();
        await _settingsWriter.FlushAsync();
        _diagnosticTimer.Stop();
        QueueDiagnosticWrite();
        await _diagnosticWrite;
    }

    internal void EmergencyCleanup()
    {
        _quitting = true;
        _brandingTimer.Stop();
        _state.Hide();
        try
        {
            _focusWatch.Stop();
            _hotkey.Dispose();
            _panel.ClosePopups();
            _tray.Visible = false;
            _tray.Dispose();
#if TEST_HARNESS
            _fixtureServer?.Stop();
#endif
            _shell.Release();
            _shell.Dispose();
        }
        catch { }
    }
}
