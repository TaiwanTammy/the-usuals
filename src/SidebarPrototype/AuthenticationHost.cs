using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    private readonly List<(string LauncherId,long Epoch,AuthSurface Window,WebView2 View)> _ownedPopups=[];
    private readonly List<(string LauncherId,WebView2 View)> _retiredViews=[];
    private int _promotedAuthViews;
    private int _nativePopupCloseEvents;
    private int _appUnloadCount;
    private long AppEpoch(string id) => _appEpochs.GetValueOrDefault(id);
    private bool IsAppEpochCurrent(string id,long epoch) => !_quitting && !_explicitlyUnloadedApps.Contains(id) && AppEpoch(id)==epoch;
    private bool IsCurrent(Launcher app,WebView2 view) => !_quitting && !_explicitlyUnloadedApps.Contains(app.Id) && _views.TryGetValue(app.Id,out var current) && ReferenceEquals(current,view);
    private void UnloadLauncher(Launcher app)
    {
        var id=app.Id; var wasActive=_state.IsVisible && _state.ActiveId==id;
        _appEpochs[id]=AppEpoch(id)+1; _explicitlyUnloadedApps.Add(id); _initializers.Remove(id); _initializationErrors.Remove(id);
        _navigationSessions.Remove(id); _desktopAgents.Remove(id); _mobileViewports.Remove(id); _identityChanges.Remove(id);
        foreach(var pending in _pendingZoom.Where(x=>x.Value.Id==id).Select(x=>x.Key).ToArray()) _pendingZoom.Remove(pending);
        if(wasActive) {
            _state.Hide(); _panel.ClearActiveApp(id); _panel.Hide(); if(IsVisible) Reserve(_settings.DockWidth,0); _previousForeground=0;
        }
        _panel.ClosePopups(id);
        var dispose=new HashSet<WebView2>();
        if(_views.Remove(id,out var current)) dispose.Add(current);
        foreach(var retired in _retiredViews.Where(x=>x.LauncherId==id).ToArray()) { dispose.Add(retired.View); _retiredViews.Remove(retired); }
        foreach(var popup in _ownedPopups.Where(x=>x.LauncherId==id).ToArray()) { popup.Window.Close(); dispose.Add(popup.View); }
        _ownedPopups.RemoveAll(x=>x.LauncherId==id);
        foreach(var view in dispose) {
            _panel.RemoveView(view); _viewOwners.Remove(view); _pendingZoom.Remove(view);
            try { view.CoreWebView2?.Stop(); } catch { }
            try { view.Dispose(); } catch { }
        }
        if(wasActive) _panel.SetStatus("App closed. Select its icon to open it again.");
        _appUnloadCount++;
    }
    private void ConfigureExternalSchemes(CoreWebView2 core,Func<bool>? applies=null)
    {
        core.LaunchingExternalUriScheme+=(_,e)=> {
            if(applies is not null && !applies()) return;
            e.Cancel=true; _externalSuppressed++;
            _panel.SetStatus("The website requested an outside application. Use another sign-in method to stay in the panel.");
            ObserveNavigation("external-protocol",false,null,NavigationDecision.Block,false,e.IsUserInitiated);
        };
    }
    private async Task ConfigureMainViewAsync(Launcher app,WebView2 view,bool keyboard,long epoch)
    {
        var core=view.CoreWebView2; var setting=GetSetting(app.Id);
        var nativeAgent=core.Settings.UserAgent; _desktopAgents[app.Id]=nativeAgent;
        var navigation=new NavigationSession(app,false,_fixture ? new Uri(_fixtureBase!) : null); _navigationSessions[app.Id]=navigation;
        core.Settings.AreDefaultScriptDialogsEnabled=true; core.Settings.AreDevToolsEnabled=false;
        core.PermissionRequested+=(_,e)=> { if(e.PermissionKind==CoreWebView2PermissionKind.Notifications) e.State=CoreWebView2PermissionState.Deny; };
        ConfigureExternalSchemes(core,()=>IsCurrent(app,view));
        core.NavigationStarting+=(_,e)=> {
            if(!IsCurrent(app,view)) return;
            var decision=navigation.Decide(e.Uri,e.NavigationId,e.IsRedirected,e.IsUserInitiated);
            ObserveNavigation(app.Id,false,e.Uri,decision,e.IsRedirected,e.IsUserInitiated);
            if(decision!=NavigationDecision.Embed) { e.Cancel=true; if(decision==NavigationDecision.External && UrlPolicy.TryParseHttp(e.Uri,out var uri)) OpenExternal(uri!); }
        };
        if(keyboard) core.NewWindowRequested+=async (_,e)=> { if(IsCurrent(app,view)) await HandlePopupRequestAsync(app,e,false); else e.Handled=true; };
        core.ProcessFailed+=(_,e)=> {
            if(!IsCurrent(app,view)) return;
            _initializationErrors[app.Id]="The browser process stopped. Retry to reopen this app.";
            if(_state.IsVisible && _state.ActiveId==app.Id) _panel.ShowFailure(app,()=>_=RetryAsync(app));
        };
        view.ZoomFactor=Math.Clamp(setting.Zoom,.5,2);
        core.Profile.PreferredColorScheme=setting.Theme==SiteTheme.Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        view.ZoomFactorChanged+=(_,_)=> {
            if(!IsCurrent(app,view) || _pendingZoom.ContainsKey(view)) return;
            if(Math.Abs(view.ZoomFactor-setting.Zoom)>.001) { setting.Zoom=view.ZoomFactor; if(_state.ActiveId==app.Id) _panel.RefreshControls(setting,_openMode); _=SaveAsync(); }
        };
        core.NavigationCompleted+=(_,e)=> {
            if(!IsCurrent(app,view)) return;
            var ok=e.IsSuccess && e.HttpStatusCode<400;
            navigation.Complete(core.Source,ok);
            ObserveNavigation(app.Id,false,core.Source,NavigationDecision.Embed,false,false,e.HttpStatusCode);
            if(e.WebErrorStatus==CoreWebView2WebErrorStatus.OperationCanceled) return;
            if(_state.IsVisible && _state.ActiveId==app.Id) _panel.SetStatus(ok ? DescribePage(app,core.Source) : NavigationError(e));
            if(UrlPolicy.IsAllowedRestore(core.Source,app)) { setting.LastUrl=core.Source; _=SaveAsync(); }
        };
        if(keyboard) { await ConfigureKeyboardAsync(app,view,core); if(!IsAppEpochCurrent(app.Id,epoch) || !IsCurrent(app,view)) return; }
        if(setting.MobileMode) {
            var width=ViewportWidth(view); var height=ViewportHeight(view);
            await BrowserIdentity.ApplyAsync(core,true,nativeAgent,width,height);
            if(!IsAppEpochCurrent(app.Id,epoch) || !IsCurrent(app,view)) return;
            _mobileViewports[app.Id]=(width,height);
        } else _mobileViewports.Remove(app.Id);
    }
    private static string NavigationError(CoreWebView2NavigationCompletedEventArgs e) => e.HttpStatusCode>=400
        ? $"The website returned HTTP {e.HttpStatusCode}. Use Reload to retry."
        : $"The page could not finish loading ({e.WebErrorStatus}). Use Reload to retry.";
    private int ViewportWidth(WebView2 view) => (int)Math.Max(320,Math.Round(view.ActualWidth>0 ? view.ActualWidth : _openWidth>0 ? _openWidth : 500));
    private int ViewportHeight(WebView2 view) => (int)Math.Max(320,Math.Round(view.ActualHeight>0 ? view.ActualHeight : 800));
    private void ScheduleActiveMobileViewportRefresh() { if(!_mobileViewportTimer.IsEnabled) _mobileViewportTimer.Start(); }
    private async Task ApplyActiveMobileViewportAsync()
    {
        if(_quitting || !_state.IsVisible || _state.ActiveId is not { } id || _panel.HasActiveAuth ||
            !GetSetting(id).MobileMode || !_views.TryGetValue(id,out var view) || view.CoreWebView2 is not { } core) return;
        var epoch=AppEpoch(id);
        var size=(ViewportWidth(view),ViewportHeight(view));
        if(_mobileViewports.TryGetValue(id,out var previous) && previous==size) return;
        try {
            await BrowserIdentity.ApplyViewportAsync(core,true,size.Item1,size.Item2);
            if(IsAppEpochCurrent(id,epoch) && ConfiguredLaunchers.Any(x=>x.Id==id) && _views.TryGetValue(id,out var current) && ReferenceEquals(current,view) && _state.IsVisible && _state.ActiveId==id && GetSetting(id).MobileMode) { _mobileViewports[id]=size; _mobileViewportUpdates++; }
        } catch(OperationCanceledException) when(!IsAppEpochCurrent(id,epoch)) { }
        catch(ObjectDisposedException) when(!IsAppEpochCurrent(id,epoch)) { }
        catch { if(IsAppEpochCurrent(id,epoch) && _state.IsVisible && _state.ActiveId==id) _panel.SetStatus("Could not update the mobile page size after resizing."); }
    }
    private async Task<CoreWebView2?> CreatePopupAsync(Launcher app,long epoch,bool explicitGoogleAuthIntent=false)
    {
        var surface=new AuthSurface(app); var web=new WebView2 { DefaultBackgroundColor=System.Drawing.Color.FromArgb(23,23,23) };
        surface.SetBrowser(web); web.CreationProperties=new CoreWebView2CreationProperties { ProfileName=app.Profile };
        _ownedPopups.Add((app.Id,epoch,surface,web)); _panel.TrackPopup(surface);
        surface.Closed+=(_,_)=> { _ownedPopups.RemoveAll(x=>ReferenceEquals(x.Window,surface)); Dispatcher.BeginInvoke(new Action(()=> { try { web.Dispose(); } catch { } if(!_quitting && _state.IsVisible && _state.ActiveId==app.Id) ScheduleActiveMobileViewportRefresh(); })); };
        try {
            if(!IsAppEpochCurrent(app.Id,epoch)) { surface.Close(); return null; }
            var environment=await _environment;
            if(!IsAppEpochCurrent(app.Id,epoch) || surface.IsClosed) { surface.Close(); return null; }
            await web.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(15));
            if(!IsAppEpochCurrent(app.Id,epoch) || surface.IsClosed) { surface.Close(); return null; }
            if(_fixture && UnloadRegressionPopupControllerReadyHook is { } popupReady) await popupReady();
            if(!IsAppEpochCurrent(app.Id,epoch) || surface.IsClosed) { surface.Close(); return null; }
            var waiting=explicitGoogleAuthIntent || _navigationSessions.TryGetValue(app.Id,out var parent) && parent.AuthenticationActive;
            var parentSource=_views.TryGetValue(app.Id,out var main) ? main.CoreWebView2?.Source : null;
            ConfigurePopup(app,web,surface,waiting || AppReturnPolicy.IsGoogleLanding(app,parentSource),epoch);
            await ConfigureKeyboardAsync(app,web,web.CoreWebView2,surface);
            if(!IsAppEpochCurrent(app.Id,epoch) || surface.IsClosed) { surface.Close(); return null; }
            web.CoreWebView2.WindowCloseRequested+=(_,_)=> { _nativePopupCloseEvents++; Dispatcher.BeginInvoke(new Action(()=> { if(!surface.IsPromoted) surface.Close(); })); };
            return web.CoreWebView2;
        } catch { surface.Close(); throw; }
    }
    private async Task HandlePopupRequestAsync(Launcher app,CoreWebView2NewWindowRequestedEventArgs e,bool ownedContext)
    {
        e.Handled=true;
        var epoch=AppEpoch(app.Id);
        if(!IsAppEpochCurrent(app.Id,epoch)) return;
        var googleAuthIntent=UrlPolicy.TryParseHttp(e.Uri,out var initialTarget) && AppReturnPolicy.IsGoogleSignInIntent(initialTarget!) ||
            _fixture && Uri.TryCreate(e.Uri,UriKind.Absolute,out var fixtureTarget) && fixtureTarget!.IsLoopback && fixtureTarget.Port==new Uri(_fixtureBase!).Port && fixtureTarget.AbsolutePath=="/google-auth-intent";
        if(!UrlPolicy.IsPopupBootstrap(e.Uri)) {
            if(!UrlPolicy.TryParseHttp(e.Uri,out var uri)) return;
            var auth=_navigationSessions.TryGetValue(app.Id,out var session) && session.AuthenticationActive;
            if(!ownedContext && !auth && !UrlPolicy.ShouldEmbedPopup(uri!,app,_fixture ? new Uri(_fixtureBase!) : null)) { OpenExternal(uri!); return; }
            if(uri!.Scheme!="https" && !(_fixture && uri.IsLoopback && uri.Port==new Uri(_fixtureBase!).Port)) return;
        }
        var deferral=e.GetDeferral();
        try { var popup=await CreatePopupAsync(app,epoch,googleAuthIntent); if(popup is not null && IsAppEpochCurrent(app.Id,epoch)) e.NewWindow=popup; }
        catch(OperationCanceledException) when(!IsAppEpochCurrent(app.Id,epoch)) { }
        catch(ObjectDisposedException) when(!IsAppEpochCurrent(app.Id,epoch)) { }
        catch { if(IsAppEpochCurrent(app.Id,epoch) && _state.IsVisible && _state.ActiveId==app.Id) _panel.SetStatus("The sign-in view could not open. Please try again."); }
        finally { deferral.Complete(); if(_fixture) UnloadRegressionPopupRequestFinished?.TrySetResult((e.Handled,e.NewWindow is not null)); }
    }
    private void ConfigurePopup(Launcher app,WebView2 web,AuthSurface surface,bool waiting,long epoch)
    {
        var core=web.CoreWebView2; var navigation=new NavigationSession(app,true,_fixture ? new Uri(_fixtureBase!) : null);
        var waitingForSignIn=waiting;
        core.Settings.AreDefaultScriptDialogsEnabled=true; core.Settings.AreDevToolsEnabled=false;
        core.PermissionRequested+=(_,e)=> { if(e.PermissionKind==CoreWebView2PermissionKind.Notifications) e.State=CoreWebView2PermissionState.Deny; };
        ConfigureExternalSchemes(core,()=>!surface.IsPromoted || !surface.MainHandlersReady);
        core.NavigationStarting+=(_,e)=> {
            if(surface.IsPromoted) return;
            if(!IsAppEpochCurrent(app.Id,epoch) || surface.IsClosed) { e.Cancel=true; return; }
            if(AppReturnPolicy.IsGoogle(app) && (UrlPolicy.TryParseHttp(e.Uri,out var intentUri) && AppReturnPolicy.IsGoogleSignInIntent(intentUri!) ||
                _fixture && Uri.TryCreate(e.Uri,UriKind.Absolute,out var fixtureIntent) && fixtureIntent!.IsLoopback && fixtureIntent.Port==new Uri(_fixtureBase!).Port && fixtureIntent.AbsolutePath=="/google-auth-intent"))
                waitingForSignIn=true;
            var decision=navigation.Decide(e.Uri,e.NavigationId,e.IsRedirected,e.IsUserInitiated); e.Cancel=decision!=NavigationDecision.Embed;
            ObserveNavigation(app.Id,true,e.Uri,decision,e.IsRedirected,e.IsUserInitiated);
            surface.SetLocation(e.Uri,e.Cancel ? "Unsupported sign-in navigation was blocked." : "Loading…");
        };
        core.SourceChanged+=(_,_)=> {
            if(surface.IsClosed || !IsAppEpochCurrent(app.Id,epoch)) return;
            if(!surface.IsPromoted || IsCurrent(app,web)) surface.SetLocation(core.Source,"Loading…");
        };
        core.NavigationCompleted+=async (_,e)=> {
            if(surface.IsPromoted || surface.IsClosed || !IsAppEpochCurrent(app.Id,epoch)) return;
            var ok=e.IsSuccess && e.HttpStatusCode<400;
            ObserveNavigation(app.Id,true,core.Source,NavigationDecision.Embed,false,false,e.HttpStatusCode);
            if(e.WebErrorStatus==CoreWebView2WebErrorStatus.OperationCanceled) return;
            surface.SetLocation(core.Source,ok ? "" : NavigationError(e));
            if(ok && AppReturnPolicy.IsAppReturn(app,core.Source,waitingForSignIn,_fixture ? new Uri(_fixtureBase!) : null)) {
                try { await PromoteAuthAsync(app,web,surface,epoch); }
                catch(OperationCanceledException) when(!IsAppEpochCurrent(app.Id,epoch)) { }
                catch(ObjectDisposedException) when(!IsAppEpochCurrent(app.Id,epoch)) { }
                catch {
                    if(!IsAppEpochCurrent(app.Id,epoch) || surface.IsClosed || (!IsCurrent(app,web) && !_ownedPopups.Any(x=>ReferenceEquals(x.Window,surface)))) return;
                    _initializationErrors[app.Id]="The signed-in page could not be attached. Use Retry to reopen it.";
                    if(_state.IsVisible && _state.ActiveId==app.Id) _panel.ShowFailure(app,()=>_=RetryAsync(app));
                }
            }
        };
        core.NewWindowRequested+=async (_,e)=> { e.Handled=true; if(!surface.IsClosed && IsAppEpochCurrent(app.Id,epoch)) await HandlePopupRequestAsync(app,e,!surface.IsPromoted); };
    }
    private async Task PromoteAuthAsync(Launcher app,WebView2 web,AuthSurface surface,long epoch)
    {
        if(surface.IsPromoted || surface.IsClosed || !IsAppEpochCurrent(app.Id,epoch)) return;
        if(_views.TryGetValue(app.Id,out var old)) _retiredViews.Add((app.Id,old));
        _views[app.Id]=web; _initializers[app.Id]=Task.CompletedTask; _initializationErrors.Remove(app.Id);
        _viewOwners[web]=app.Id;
        _ownedPopups.RemoveAll(x=>ReferenceEquals(x.Window,surface));
        _panel.PromotePopup(surface,web); _promotedAuthViews++;
        await ConfigureMainViewAsync(app,web,false,epoch);
        if(_fixture && UnloadRegressionPromotionConfiguredHook is { } promotionConfigured) await promotionConfigured();
        if(!IsAppEpochCurrent(app.Id,epoch) || !IsCurrent(app,web)) return;
        surface.MainHandlersReady=true;
        if(_state.IsVisible && _state.ActiveId==app.Id) _panel.SetStatus(DescribePage(app,web.CoreWebView2.Source));
        if(GetSetting(app.Id).MobileMode) web.CoreWebView2.Reload();
    }
}
