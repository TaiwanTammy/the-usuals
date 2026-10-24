using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SidebarPrototype.Shell;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    private async Task PrepareEmptyViewsAsync()
    {
        if (_quitting || _state.IsVisible)
            return;
        _panel.ShowActivated = false;
        _panel.Topmost = false;
        _panel.Left = -12000;
        _panel.Top = 0;
        _panel.Width = 500;
        _panel.Height = 700;
        _panel.Show();
        try
        {
            foreach (var launcher in Launchers.All)
            {
                if (_quitting || _state.IsVisible || _lastLauncherId is not null)
                    break;
                if (_explicitlyUnloadedApps.Contains(launcher.Id))
                    continue;
                var epoch = AppEpoch(launcher.Id);
                EnsureView(launcher);
                _panel.SetActive(launcher, GetSetting(launcher.Id), _views[launcher.Id]);
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!IsAppEpochCurrent(launcher.Id, epoch))
                    continue;
                await StartInitialization(launcher, _views[launcher.Id]);
                if (!IsAppEpochCurrent(launcher.Id, epoch))
                    continue;
            }
        }
        finally
        {
            if (!_quitting)
            {
                if (!_state.IsVisible)
                    _panel.Hide();
                _panel.ShowActivated = !_options.BrowserOnly;
            }
        }
    }

    private readonly List<InteractionMetric> _interactionMetrics = [];

    private void RecordInteraction(string launcher, string operation, long started)
    {
        if (_interactionMetrics.Count >= 128)
            _interactionMetrics.RemoveAt(0);
        _interactionMetrics.Add(
            new(launcher, operation, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds)
        );
    }

    private async Task ActivateFromDockAsync(Launcher launcher)
    {
        var epoch = AppEpoch(launcher.Id);
        try
        {
            await ActivateAsync(launcher);
        }
        catch (OperationCanceledException) when (!IsAppEpochCurrent(launcher.Id, epoch)) { }
        catch (ObjectDisposedException) when (!IsAppEpochCurrent(launcher.Id, epoch)) { }
        catch (Exception error)
        {
            if (IsAppEpochCurrent(launcher.Id, epoch) && _state.IsVisible && _state.ActiveId == launcher.Id)
                _panel.SetStatus($"Could not open {launcher.Label}: {error.GetType().Name}");
        }
    }

    private async Task ActivateAsync(Launcher launcher, bool toggleSame = true)
    {
        var clickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (toggleSame && _state.IsVisible && _state.ActiveId == launcher.Id)
        {
            ClosePanel(!_options.TransitionTest);
            return;
        }
        if (_explicitlyUnloadedApps.Remove(launcher.Id))
            _appEpochs[launcher.Id] = AppEpoch(launcher.Id) + 1;
        var wasOpen = _state.IsVisible;
        if (!wasOpen)
            _previousForeground = ForegroundWindowHelper.Capture();
        _lastLauncherId = launcher.Id;
        if (!IsVisible)
            Show();
        var generation = _state.BeginSwitch(launcher.Id);
        MarkActiveDockButton(launcher.Id);
        var setting = GetSetting(launcher.Id);
        if (!wasOpen)
        {
            _openMode = setting.Mode;
            _openWidth = GetPanelWidth(setting);
            Reserve(_settings.DockWidth, _openMode == PanelMode.Split ? _openWidth : 0);
            if (_options.BrowserOnly || (_options.TransitionTest && !_options.TransitionNative))
                ApplyLayout();
        }
        _panel.SetStatus($"Opening {launcher.Label}…");
        _panel.Show();
        if (!_options.BrowserOnly && !_options.LayoutOnly && !_options.TransitionTest)
            _panel.Activate();
        RecordInteraction(launcher.Id, "panel-shown", clickStarted);
        if (_options.LayoutOnly)
            return;
        _ = Dispatcher.BeginInvoke(
            new Action(() => RecordInteraction(launcher.Id, "render-queue", clickStarted)),
            DispatcherPriority.Render
        );
        if (!_views.ContainsKey(launcher.Id))
            await Dispatcher.Yield(DispatcherPriority.Background);
        if (_quitting || !_state.CanPresent(launcher.Id, generation))
            return;
        EnsureView(launcher);
        var view = _views[launcher.Id];
        _panel.SetActive(launcher, setting, view);
        _panel.RefreshControls(setting, _openMode);
        RecordInteraction(launcher.Id, "view-created", clickStarted);
        await StartInitialization(launcher, view);
        RecordInteraction(launcher.Id, "controller-ready", clickStarted);
        if (_quitting || !_views.TryGetValue(launcher.Id, out var current) || !ReferenceEquals(current, view))
            return;
        if (_initializationErrors.TryGetValue(launcher.Id, out var initializationError))
        {
            if (_state.CanPresent(launcher.Id, generation))
            {
                _panel.ShowFailure(launcher, () => _ = RetryAsync(launcher));
                _panel.SetError(initializationError);
                _panel.SetStatus("Could not open this app. Use Retry.");
            }
            return;
        }
        if (view.CoreWebView2 is { } core)
        {
            var target = _options.Fixture
                ? new Uri(_fixtureBase + launcher.Id + "/")
                : (
                    UrlPolicy.IsAllowedRestore(setting.LastUrl, launcher)
                        ? new Uri(setting.LastUrl!)
                        : launcher.StartUri
                );
            if (_options.Fixture)
                File.AppendAllText(
                    Path.Combine(_root, "fixture-trace.log"),
                    $"{launcher.Id} source={core.Source} target={target} panel={_panel.IsVisible},{_panel.ActualWidth},{_panel.ActualHeight}\n"
                );
            if (
                string.IsNullOrWhiteSpace(core.Source)
                || core.Source == "about:blank"
                || AppReturnPolicy.IsGoogleLanding(launcher, core.Source)
            )
                core.Navigate(target.AbsoluteUri);
            RecordInteraction(launcher.Id, "navigation-requested", clickStarted);
            if (_state.CanPresent(launcher.Id, generation))
                _panel.SetStatus(
                    core.Source == "about:blank" ? $"Loading {launcher.Label}…" : DescribePage(launcher, core.Source)
                );
        }
    }

    private void EnsureView(Launcher launcher)
    {
        if (_explicitlyUnloadedApps.Contains(launcher.Id))
            return;
        if (_views.ContainsKey(launcher.Id))
            return;
        var view = new WebView2
        {
            Visibility = Visibility.Collapsed,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(23, 23, 23),
        };
        _views[launcher.Id] = view;
        _viewOwners[view] = launcher.Id;
        _panel.AddView(view);
    }

    private Task StartInitialization(Launcher launcher, WebView2 view)
    {
        if (_initializers.TryGetValue(launcher.Id, out var existing))
            return existing;
        var epoch = AppEpoch(launcher.Id);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _initializers[launcher.Id] = completion.Task;
        _ = InitializeAndCompleteAsync();
        return completion.Task;
        async Task InitializeAndCompleteAsync()
        {
            try
            {
                await InitializeViewAsync(launcher, view, epoch);
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }
    }

    private async Task InitializeViewAsync(Launcher launcher, WebView2 view, long epoch)
    {
        try
        {
            var env = await _environment;
            if (!IsAppEpochCurrent(launcher.Id, epoch) || !IsCurrent(launcher, view))
                return;
            view.CreationProperties = new CoreWebView2CreationProperties { ProfileName = launcher.Profile };
            await view.EnsureCoreWebView2Async(env);
            if (!IsAppEpochCurrent(launcher.Id, epoch) || !IsCurrent(launcher, view))
                return;
#if TEST_HARNESS
            if (_options.Fixture && UnloadRegressionMainControllerReadyHook is { } mainReady)
                await mainReady();
#endif
            if (!IsAppEpochCurrent(launcher.Id, epoch) || !IsCurrent(launcher, view))
                return;
            await ConfigureMainViewAsync(launcher, view, true, epoch);
        }
        catch (Exception ex)
        {
            if (!IsAppEpochCurrent(launcher.Id, epoch) || !IsCurrent(launcher, view))
                return;
            _initializationErrors[launcher.Id] = ex.Message;
            if (_options.Fixture)
                File.AppendAllText(Path.Combine(_root, "fixture-trace.log"), ex.ToString() + "\n");
            if (_state.IsVisible && _state.ActiveId == launcher.Id)
            {
                _panel.ShowFailure(launcher, () => _ = RetryAsync(launcher));
                _panel.SetError(ex.Message);
            }
        }
    }

    private async Task RetryAsync(Launcher launcher)
    {
        _panel.ClearFailure();
        if (_views.Remove(launcher.Id, out var failed))
        {
            _panel.RemoveView(failed);
            try
            {
                failed.Dispose();
            }
            catch { }
        }
        _initializers.Remove(launcher.Id);
        _initializationErrors.Remove(launcher.Id);
        EnsureView(launcher);
        await ActivateAsync(launcher, false);
    }

    private async Task ConfigureKeyboardAsync(
        Launcher launcher,
        WebView2 view,
        CoreWebView2 core,
        AuthSurface? popup = null
    )
    {
        view.KeyDown += (_, e) =>
        {
            var mods = e.KeyboardDevice.Modifiers;
            if (
                !mods.HasFlag(System.Windows.Input.ModifierKeys.Control)
                || mods.HasFlag(System.Windows.Input.ModifierKeys.Alt)
            )
                return;
            var next = e.Key switch
            {
                System.Windows.Input.Key.Add or System.Windows.Input.Key.OemPlus => Math.Min(
                    2,
                    GetSetting(launcher.Id).Zoom + .1
                ),
                System.Windows.Input.Key.Subtract or System.Windows.Input.Key.OemMinus => Math.Max(
                    .5,
                    GetSetting(launcher.Id).Zoom - .1
                ),
                System.Windows.Input.Key.D0 or System.Windows.Input.Key.NumPad0 => 1,
                _ => double.NaN,
            };
            if (!double.IsFinite(next))
                return;
            QueueZoom(launcher.Id, next, view);
            e.Handled = true;
        };
        core.WebMessageReceived += (_, e) =>
        {
            string? message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch
            {
                return;
            }
            if (_quitting)
                return;
            if (message == AuthSurface.WindowCloseMessage)
            {
                _authCloseBridgeMessages++;
                // A popup's window.close is routed through this narrow message bridge so
                // WebView2's WPF wrapper cannot close the panel window that owns every view.
                if (popup is not null && !popup.IsPromoted)
                    popup.Close();
                return;
            }
            if (message != "sidebar-unclaimed-escape")
                return;
            if (popup is not null && !popup.IsPromoted)
                popup.Close();
            else if (_state.IsVisible && _state.ActiveId == launcher.Id)
                ClosePanel();
        };
        var closeBridge = popup is null
            ? ""
            : $$"""
                if (window.top === window) {
                    try {
                        Object.defineProperty(window, 'close', {
                            configurable: true,
                            value: function () { window.chrome.webview.postMessage('{{AuthSurface.WindowCloseMessage}}'); }
                        });
                    } catch (_) { }
                }
                """;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            closeBridge
                + """
                document.addEventListener('keydown', e => {
                    if (e.key !== 'Escape' || !e.isTrusted) return;
                    const modal = !!document.querySelector('dialog[open], [role="dialog"], [role="alertdialog"]');
                    setTimeout(() => {
                        if (!e.defaultPrevented && !modal) window.chrome.webview.postMessage('sidebar-unclaimed-escape');
                    }, 0);
                }, true);
                """
        );
    }

    private void OpenExternal(Uri uri, bool explicitlyRequested = false)
    {
        if (!explicitlyRequested)
        {
            _externalSuppressed++;
            ObserveNavigation("outside-link", false, uri.AbsoluteUri, NavigationDecision.Block, false, false);
            _panel.OfferExternalLink(uri, () => OpenExternal(uri, true));
            return;
        }
        _externalLaunchRequests++;
        if (_options.Fixture)
            return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
            );
        }
        catch
        {
            _panel.SetStatus("The default browser could not open.");
        }
    }

    private int _externalSuppressed;

    private static string DescribePage(Launcher launcher, string uri) =>
        UrlPolicy.TryParseHttp(uri, out var parsed) ? $"{launcher.Label} · {parsed!.IdnHost}" : launcher.Label;

    private readonly List<object> _navigationObservations = [];
    private int _authCloseBridgeMessages;

    private void ObserveNavigation(
        string id,
        bool popup,
        string? value,
        NavigationDecision decision,
        bool redirected,
        bool userInitiated,
        int? status = null
    )
    {
        var host = UrlPolicy.TryParseHttp(value, out var uri) ? uri!.IdnHost : "(non-http)";
        if (_navigationObservations.Count >= 128)
            _navigationObservations.RemoveAt(0);
        _navigationObservations.Add(
            new
            {
                id,
                popup,
                host,
                decision = decision.ToString(),
                redirected,
                userInitiated,
                status,
            }
        );
        _diagnosticTimer.Stop();
        _diagnosticTimer.Start();
    }

    private void QueueDiagnosticWrite()
    {
        var snapshot = JsonSerializer.Serialize(
            new
            {
                build = "update-6",
                externalLaunchRequests = _externalLaunchRequests,
                automaticExternalLaunchesSuppressed = _externalSuppressed,
                appBarSetPosCalls = _shell.AppBarSetPosCallCount,
                events = _navigationObservations.ToArray(),
            },
            new JsonSerializerOptions { WriteIndented = true }
        );
        _diagnosticWrite = _diagnosticWrite
            .ContinueWith(
                async previous =>
                {
                    _ = previous.Exception;
                    try
                    {
                        await File.WriteAllTextAsync(Path.Combine(_root, "navigation-diagnostics.json"), snapshot);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                },
                TaskScheduler.Default
            )
            .Unwrap();
    }
}
