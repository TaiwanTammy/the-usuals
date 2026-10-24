using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    internal void OnToolbar(string action)
    {
        var commandStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_state.ActiveId is not { } id || !_views.TryGetValue(id, out var v))
            return;
        v = _panel.ActiveAuthView(id) ?? v;
        var s = GetSetting(id);
        switch (action)
        {
            case "close":
                ClosePanel();
                break;
            case "back":
                if (v.CoreWebView2?.CanGoBack == true)
                    v.CoreWebView2.GoBack();
                break;
            case "forward":
                if (v.CoreWebView2?.CanGoForward == true)
                    v.CoreWebView2.GoForward();
                break;
            case "reload":
                v.CoreWebView2?.Reload();
                break;
            case "mode":
                _openMode = _openMode == PanelMode.Overlay ? PanelMode.Split : PanelMode.Overlay;
                s.Mode = _openMode;
                Reserve(_settings.DockWidth, _openMode == PanelMode.Split ? _openWidth : 0);
                if (_options.BrowserOnly || (_options.TransitionTest && !_options.TransitionNative))
                    ApplyLayout();
                _panel.RefreshControls(s, _openMode);
                _ = SaveAsync();
                break;
            case "top":
                _settings.Topmost = !_settings.Topmost;
                Topmost = _settings.Topmost;
                _panel.Topmost = _settings.Topmost && !_shell.IsFullscreenActive;
                _shell.SetTopmost(_settings.Topmost);
                _panel.RefreshControls(s, _openMode);
                _ = SaveAsync();
                break;
            case "theme":
                s.Theme = s.Theme == SiteTheme.Dark ? SiteTheme.Light : SiteTheme.Dark;
                var profile = ConfiguredLaunchers.Single(x => x.Id == id).Profile;
                foreach (var sibling in ConfiguredLaunchers.Where(x => x.Profile == profile))
                    GetSetting(sibling.Id).Theme = s.Theme;
                if (v.CoreWebView2 is { } core)
                    core.Profile.PreferredColorScheme =
                        s.Theme == SiteTheme.Dark
                            ? CoreWebView2PreferredColorScheme.Dark
                            : CoreWebView2PreferredColorScheme.Light;
                _panel.RefreshControls(s, _openMode);
                _ = SaveAsync();
                break;
            case "zoom+":
                QueueZoom(id, Math.Min(2, s.Zoom + .1), v);
                break;
            case "zoom-":
                QueueZoom(id, Math.Max(.5, s.Zoom - .1), v);
                break;
            case "zoom0":
                QueueZoom(id, 1, v);
                break;
            case "identity":
                _ = ToggleIdentityAsync(id, v);
                break;
            case "external":
                if (_panel.HasActiveAuth)
                {
                    _panel.SetStatus("Finish or cancel sign-in in this panel first.");
                    break;
                }
                if (UrlPolicy.TryParseHttp(v.Source?.AbsoluteUri, out var uri))
                    OpenExternal(uri!, true);
                break;
        }
        if (action.StartsWith("zoom"))
            RecordInteraction(id, "zoom-command", commandStarted);
    }

    private readonly HashSet<string> _identityChanges = [];

    private async Task ToggleIdentityAsync(string id, WebView2 view)
    {
        if (
            _panel.HasActiveAuth
            || _navigationSessions.TryGetValue(id, out var activeSession) && activeSession.AuthenticationActive
        )
        {
            _panel.SetStatus("Finish or cancel sign-in before changing browser mode.");
            return;
        }
        if (
            view.CoreWebView2 is not { } core
            || !_desktopAgents.TryGetValue(id, out var nativeAgent)
            || !_identityChanges.Add(id)
        )
            return;
        var epoch = AppEpoch(id);
        var setting = GetSetting(id);
        var mobile = !setting.MobileMode;
        var app = ConfiguredLaunchers.Single(x => x.Id == id);
        var target =
            _options.Fixture || UrlPolicy.IsAllowedRestore(core.Source, app) ? core.Source : app.StartUri.AbsoluteUri;
        try
        {
            core.Stop();
            var viewport = (ViewportWidth(view), ViewportHeight(view));
            await BrowserIdentity.ApplyAsync(core, mobile, nativeAgent, viewport.Item1, viewport.Item2);
            if (!IsAppEpochCurrent(id, epoch) || !IsCurrent(app, view))
                return;
            setting.MobileMode = mobile;
            if (mobile)
                _mobileViewports[id] = viewport;
            else
                _mobileViewports.Remove(id);
            if (_state.IsVisible && _state.ActiveId == id)
                _panel.RefreshControls(setting, _openMode);
            await SaveAsync();
            core.Navigate(target);
        }
        catch (OperationCanceledException) when (!IsAppEpochCurrent(id, epoch)) { }
        catch (ObjectDisposedException) when (!IsAppEpochCurrent(id, epoch)) { }
        catch
        {
            if (IsAppEpochCurrent(id, epoch) && _state.IsVisible && _state.ActiveId == id)
                _panel.SetStatus("Could not change browser mode. Please try again.");
        }
        finally
        {
            if (AppEpoch(id) == epoch)
                _identityChanges.Remove(id);
        }
    }

    private void QueueZoom(string id, double target, WebView2? source = null)
    {
        GetSetting(id).Zoom = target;
        if (_state.ActiveId == id)
            _panel.RefreshControls(GetSetting(id), _openMode);
        if (_views.TryGetValue(id, out var main))
            _pendingZoom[main] = (id, target);
        if (source is not null)
            _pendingZoom[source] = (id, target);
        _panel.SetStatus($"Zoom {Math.Round(target * 100)}%");
        _zoomTimer.Start();
        _ = SaveAsync();
    }

    private void ApplyPendingZoom()
    {
        _zoomTimer.Stop();
        var requests = _pendingZoom.ToArray();
        _pendingZoom.Clear();
        foreach (var (view, request) in requests)
        {
            if (_quitting)
                return;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                view.ZoomFactor = request.Target;
                RecordInteraction(request.Id, "zoom-apply", started);
            }
            catch (ObjectDisposedException) { }
        }
    }
}
