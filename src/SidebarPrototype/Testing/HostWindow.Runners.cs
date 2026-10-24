using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    private WorkAreaSnapshot? _workAreaBefore;

    // Called at the end of the constructor; returns true when a regression run owns startup.
    private bool AttachTestHarness()
    {
        if (_options.Fixture && !_options.LayoutOnly)
            StartFixture();
        if (_options.UiTest)
            Loaded += async (_, _) => await RunUiRegressionAsync();
        else if (_options.UnloadOnlyTest)
            Loaded += async (_, _) => await RunUnloadOnlyAsync();
        else if (_options.Smoke)
            Loaded += async (_, _) =>
            {
                if (_options.LayoutOnly)
                    await RunLayoutRegressionAsync();
                else if (_options.TransitionTest)
                    await RunTransitionRegressionAsync();
                else
                    await RunSmokeAsync();
            };
        else
            return false;
        return true;
    }

    private async Task RunUnloadOnlyAsync()
    {
        var results = new Dictionary<string, object?>
        {
            ["mode"] = "fixture-unload-regression",
            ["startedUtc"] = DateTime.UtcNow,
        };
        try
        {
            await PrepareEmptyViewsAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var gmail = Launchers.All.Single(x => x.Id == "gmail-a");
            await ActivateAsync(gmail, false).WaitAsync(TimeSpan.FromSeconds(20));
            await WaitDocumentAsync(_views[gmail.Id]);
            var previousMarker = await EvalAsync(_views[gmail.Id], "localStorage.getItem('sidebar-unload-marker')");
            var previousCookie = await EvalAsync(_views[gmail.Id], "document.cookie");
            results["processStartRestoredGmailIdentity"] = new
            {
                zoom = _views[gmail.Id].ZoomFactor,
                configuredZoom = GetSetting(gmail.Id).Zoom,
                mobile = GetSetting(gmail.Id).MobileMode,
                agent = await EvalAsync(_views[gmail.Id], "navigator.userAgent.includes('Mobile')"),
                marker = JsonSerializer.Deserialize<string?>(previousMarker),
                cookie = previousCookie,
            };
            await RunUnloadRegressionChecksAsync(results);
            await SaveAsync();
            results["status"] = "completed";
        }
        catch (Exception error)
        {
            results["status"] = "failed";
            results["error"] = error.GetType().Name;
            results["diagnosticMessage"] = error.Message;
            results["diagnosticStack"] = error.StackTrace;
        }
        await File.WriteAllTextAsync(
            Path.Combine(_root, "unload-evidence.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
        );
        Quit(results["status"]?.ToString() == "completed" ? 0 : 1);
    }

    private async Task RunSmokeAsync()
    {
        var started = DateTime.UtcNow;
        var results = new Dictionary<string, object?> { ["mode"] = "fixture-smoke", ["startedUtc"] = started };
        void Stage(string stage)
        {
            results["stage"] = stage;
            File.WriteAllText(Path.Combine(_root, "smoke-progress.txt"), stage);
        }
        try
        {
            if (_options.PrepareBrowsers)
            {
                Stage("prepare-empty-views");
                var preparationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                await PrepareEmptyViewsAsync().WaitAsync(TimeSpan.FromSeconds(45));
                results["preparationMilliseconds"] = System
                    .Diagnostics.Stopwatch.GetElapsedTime(preparationStarted)
                    .TotalMilliseconds;
                var emptyViewsPrepared =
                    _views.Count == Launchers.All.Length
                    && _views.Values.All(v => v.CoreWebView2?.Source == "about:blank");
                results["emptyViewsPreparedWithoutWebsiteNavigation"] = emptyViewsPrepared;
                if (!emptyViewsPrepared)
                    throw new InvalidOperationException(
                        "Browser preparation navigated a website or left a controller unprepared"
                    );
                var restoredSettings = _views.All(pair =>
                    Math.Abs(pair.Value.ZoomFactor - GetSetting(pair.Key).Zoom) < .001
                    && pair.Value.CoreWebView2.Settings.UserAgent.Contains("Mobile") == GetSetting(pair.Key).MobileMode
                );
                results["perAppZoomAndIdentityRestoredIntoNewControllers"] = restoredSettings;
                results["initialControllerPreferences"] = _views
                    .Select(pair => new
                    {
                        app = pair.Key,
                        zoom = pair.Value.ZoomFactor,
                        mobile = GetSetting(pair.Key).MobileMode,
                    })
                    .ToArray();
                if (!restoredSettings)
                    throw new InvalidOperationException(
                        "Fresh browser controllers did not restore each app's saved zoom/identity"
                    );
            }
            else
                results["preparation"] = "Disabled for cold-controller comparison";
            Stage("first-launcher");
            var a = Launchers.All[0];
            await ActivateAsync(a).WaitAsync(TimeSpan.FromSeconds(15));
            await WaitDocumentAsync(_views[a.Id]);
            var toolbar320 = _panel.CaptureToolbar(Path.Combine(_root, "toolbar-320.png"), 320);
            var toolbar500 = _panel.CaptureToolbar(Path.Combine(_root, "toolbar-500.png"), 500);
            results["toolbarRender"] = new { at320 = toolbar320, at500 = toolbar500 };
            using (var toolbarDoc = JsonDocument.Parse(JsonSerializer.Serialize(toolbar320)))
            {
                var t = toolbarDoc.RootElement;
                if (
                    !t.GetProperty("compact").GetBoolean()
                    || t.GetProperty("toolbarWidth").GetDouble() > 312
                    || !t.GetProperty("visibleControls").GetBoolean()
                    || !t.GetProperty("noOverflow").GetBoolean()
                    || t.GetProperty("toolbarHeight").GetDouble() > 36
                )
                    throw new InvalidOperationException(
                        "Compact Edgebar controls overflow or exceed the 36 DIP height limit"
                    );
            }
            _views[a.Id].CoreWebView2.IsMuted = true;
            if (GetSetting(a.Id).MobileMode)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                ScheduleActiveMobileViewportRefresh();
                await Task.Delay(350);
            }
            Stage("popup-and-audio");
            var instance = JsonSerializer.Deserialize<string>(await EvalAsync(_views[a.Id], "window.instanceId"));
            await EvalAsync(
                _views[a.Id],
                "window.fixtureAudioReady='pending';document.querySelector('#audio').play().then(()=>window.fixtureAudioReady='started',()=>window.fixtureAudioReady='failed');true"
            );
            var audioStarted =
                (await WaitForValueAsync(_views[a.Id], "window.fixtureAudioReady", "started", 5000)) ? "true" : "false";
            var audioClockStarted = await WaitForValueAsync(
                _views[a.Id],
                "String(document.querySelector('#audio').currentTime>0)",
                "true",
                5000
            );
            results["audioClockStartedBeforePopup"] = audioClockStarted;
            if (!audioClockStarted)
                throw new InvalidOperationException("Fixture audio clock did not start before popup checks");
            await EvalAsync(
                _views[a.Id],
                "window.addEventListener('message',e=>{if(typeof e.data==='string')window.fixturePopup=e.data});window.fixtureProxy=window.open('/popup','login-popup');localStorage.setItem('sidebar-fixture-marker','shared-account-a');document.querySelector('#input').focus();document.querySelector('#input').value='retained-input';window.scrollTo(0,700);true"
            );
            var popupPassed = await WaitForValueAsync(_views[a.Id], "window.fixturePopup", "fixture-popup-ok", 5000);
            var popupCloseBridgeInjected = await EvalAsync(_views[a.Id], "window.fixtureCloseBridge===true") == "true";
            results["popupWindowCloseBridgeInjected"] = popupCloseBridgeInjected;
            var popupProxyClosed = await WaitForValueAsync(
                _views[a.Id],
                "String(window.fixtureProxy && window.fixtureProxy.closed)",
                "true",
                3000
            );
            results["popupWindowProxyReportsClosed"] = popupProxyClosed;
            var blankPopupProxy = await EvalAsync(
                _views[a.Id],
                "(()=>{window.fixturePopup='';window.fixtureBlankProxy=window.open('about:blank','blank-auth');if(window.fixtureBlankProxy)window.fixtureBlankProxy.location.href='/popup';return !!window.fixtureBlankProxy})()"
            );
            var blankPopupPassed =
                blankPopupProxy == "true"
                && await WaitForValueAsync(_views[a.Id], "window.fixturePopup", "fixture-popup-ok", 5000);
            var blankProxyClosed =
                blankPopupProxy == "true"
                && await WaitForValueAsync(
                    _views[a.Id],
                    "String(window.fixtureBlankProxy && window.fixtureBlankProxy.closed)",
                    "true",
                    3000
                );
            results["blankPopupWindowProxyReportsClosed"] = blankProxyClosed;
            await EvalAsync(_views[a.Id], "window.fixturePopup='';window.open('/auth-start','auth-chain');true");
            var postRedirectPopupPassed = await WaitForValueAsync(
                _views[a.Id],
                "window.fixturePopup",
                "fixture-auth-complete",
                10000
            );
            results["crossHostPopupPost307CallbackPassed"] = postRedirectPopupPassed;
            results["panelVisibleAfterPopupCloses"] = _panel.IsVisible && _state.IsVisible;
            if (!(bool)results["panelVisibleAfterPopupCloses"]!)
                throw new InvalidOperationException("Browser popup close hid its hosting panel");
            if (!postRedirectPopupPassed || _externalLaunchRequests != 0)
                throw new InvalidOperationException(
                    "Popup POST/redirect/callback chain escaped or lost its opener/body"
                );
            var audioTime = await EvalAsync(_views[a.Id], "document.querySelector('#audio').currentTime");
            var visibleAudioTimer = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(2000);
            visibleAudioTimer.Stop();
            var audioAfterPopupClose = await EvalAsync(_views[a.Id], "document.querySelector('#audio').currentTime");
            var audioContinuesAfterPopupClose =
                JsonDocument.Parse(audioAfterPopupClose).RootElement.GetDouble()
                > JsonDocument.Parse(audioTime).RootElement.GetDouble();
            var visibleAudioDelta =
                JsonDocument.Parse(audioAfterPopupClose).RootElement.GetDouble()
                - JsonDocument.Parse(audioTime).RootElement.GetDouble();
            var visibleAudioProgress = visibleAudioDelta >= visibleAudioTimer.Elapsed.TotalSeconds * .5;
            results["audioAdvancesAfterPopupCloseWhilePanelVisible"] = audioContinuesAfterPopupClose;
            results["audioVisibleProgress"] = new
            {
                elapsedMs = visibleAudioTimer.Elapsed.TotalMilliseconds,
                deltaSeconds = visibleAudioDelta,
                hostMuted = _views[a.Id].CoreWebView2.IsMuted,
                elementUnmuted = await EvalAsync(_views[a.Id], "!document.querySelector('#audio').muted"),
            };
            Stage("account-a-sharing");
            var hiddenAudioTimer = System.Diagnostics.Stopwatch.StartNew();
            ClosePanel();
            await ActivateAsync(Launchers.All[1]);
            await WaitDocumentAsync(_views[Launchers.All[1].Id]);
            await Task.Delay(2000);
            var audioContinued = await EvalAsync(_views[a.Id], "document.querySelector('#audio').currentTime");
            hiddenAudioTimer.Stop();
            var shared = await EvalAsync(_views[Launchers.All[1].Id], "localStorage.getItem('sidebar-fixture-marker')");
            Stage("account-b-isolation");
            await ActivateAsync(Launchers.All[2]);
            await WaitDocumentAsync(_views[Launchers.All[2].Id]);
            var isolated = await EvalAsync(
                _views[Launchers.All[2].Id],
                "localStorage.getItem('sidebar-fixture-marker')"
            );
            Stage("retained-state");
            var warmStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await ActivateAsync(a);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            results["warmActivationMilliseconds"] = System
                .Diagnostics.Stopwatch.GetElapsedTime(warmStart)
                .TotalMilliseconds;
            var restored = await EvalAsync(
                _views[a.Id],
                "JSON.stringify({instance:window.instanceId,input:document.querySelector('#input').value,scroll:Math.round(window.scrollY)})"
            );
            var view = JsonDocument.Parse(JsonSerializer.Deserialize<string>(restored)!);
            var instanceKept = view.RootElement.GetProperty("instance").GetString() == instance;
            var inputKept = view.RootElement.GetProperty("input").GetString() == "retained-input";
            var scrollKept = view.RootElement.GetProperty("scroll").GetInt32() >= 650;
            var profileShared = JsonSerializer.Deserialize<string?>(shared) == "shared-account-a";
            var profileIsolated = JsonSerializer.Deserialize<string?>(isolated) != "shared-account-a";
            var hiddenTime = JsonDocument.Parse(audioContinued).RootElement.GetDouble();
            var beforeHidden = JsonDocument.Parse(audioAfterPopupClose).RootElement.GetDouble();
            var audioAdvancedWhileHidden = hiddenTime > beforeHidden;
            var hiddenAudioDelta = hiddenTime - beforeHidden;
            var hiddenAudioProgress = hiddenAudioDelta >= hiddenAudioTimer.Elapsed.TotalSeconds * .5;
            results["authWindowCloseBridgeMessages"] = _authCloseBridgeMessages;
            var popupCloseBridgeDelivered = _authCloseBridgeMessages >= 3;
            results["nativePopupCloseEvents"] = _nativePopupCloseEvents;
            results["panelWindowCloseAttempts"] = _panel.CloseAttemptCount;
            var hostCloseCountersStayedZero = _nativePopupCloseEvents == 0 && _panel.CloseAttemptCount == 0;
            results["audioBeforeHidden"] = beforeHidden;
            results["audioAfterHidden"] = hiddenTime;
            results["audioHiddenProgress"] = new
            {
                elapsedMs = hiddenAudioTimer.Elapsed.TotalMilliseconds,
                deltaSeconds = hiddenAudioDelta,
                ratio = hiddenAudioDelta / hiddenAudioTimer.Elapsed.TotalSeconds,
                passes = hiddenAudioProgress,
                hostMuted = _views[a.Id].CoreWebView2.IsMuted,
                elementUnmuted = await EvalAsync(_views[a.Id], "!document.querySelector('#audio').muted"),
            };
            results["audioState"] = await EvalAsync(
                _views[a.Id],
                "JSON.stringify({paused:document.querySelector('#audio').paused,ready:document.querySelector('#audio').readyState,error:document.querySelector('#audio').error?.code})"
            );
            results["retainedState"] = view.RootElement.Clone();
            results["checks"] = new
            {
                instanceKept,
                inputKept,
                scrollKept,
                profileShared,
                profileIsolated,
                popupPassed,
                blankPopupPassed,
                popupCloseBridgeInjected,
                popupCloseBridgeDelivered,
                popupProxyClosed,
                blankProxyClosed,
                hostCloseCountersStayedZero,
                audioStarted = JsonSerializer.Deserialize<bool>(audioStarted),
                audioContinuesAfterPopupClose,
                visibleAudioProgress,
                audioAdvancedWhileHidden,
                hiddenAudioProgress,
            };
            if (
                !(
                    instanceKept
                    && inputKept
                    && scrollKept
                    && profileShared
                    && profileIsolated
                    && popupPassed
                    && blankPopupPassed
                    && popupCloseBridgeInjected
                    && popupCloseBridgeDelivered
                    && popupProxyClosed
                    && blankProxyClosed
                    && hostCloseCountersStayedZero
                    && JsonSerializer.Deserialize<bool>(audioStarted)
                    && audioContinuesAfterPopupClose
                    && visibleAudioProgress
                    && audioAdvancedWhileHidden
                    && hiddenAudioProgress
                )
            )
                throw new InvalidOperationException("fixture browser assertion failed");
            Stage("zoom-burst");
            var zoomStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var z = 0; z < 10; z++)
                OnToolbar("zoom+");
            results["zoomBurstCommandMilliseconds"] = System
                .Diagnostics.Stopwatch.GetElapsedTime(zoomStarted)
                .TotalMilliseconds;
            await Task.Delay(150);
            var zoomBurstPassed =
                Math.Abs(_views[a.Id].ZoomFactor - 2) < 0.001 && Math.Abs(GetSetting(a.Id).Zoom - 2) < 0.001;
            OnToolbar("zoom0");
            await Task.Delay(100);
            await _settingsWriter.FlushAsync();
            var zoomResetPassed =
                Math.Abs(_views[a.Id].ZoomFactor - 1) < 0.001
                && AppSettings.SafeLoad(Path.Combine(_root, "settings.json")).Launchers[a.Id].Zoom == 1;
            results["zoomBurstPassed"] = zoomBurstPassed;
            results["zoomResetPersisted"] = zoomResetPassed;
            if (!zoomBurstPassed || !zoomResetPassed)
                throw new InvalidOperationException("Zoom burst/reset did not apply and persist");
            Stage("per-app-zoom-and-identity");
            QueueZoom(a.Id, 1.3);
            await Task.Delay(80);
            await ActivateAsync(Launchers.All[1]);
            QueueZoom(Launchers.All[1].Id, .8);
            await Task.Delay(80);
            await ActivateAsync(a);
            var independentZoom =
                Math.Abs(_views[a.Id].ZoomFactor - 1.3) < .001
                && Math.Abs(_views[Launchers.All[1].Id].ZoomFactor - .8) < .001;
            var nativeBoundsBeforeIdentity = new
            {
                Left,
                Top,
                Width,
                Height,
                PanelWidth = _panel.Width,
                PanelHeight = _panel.Height,
            };
            var reservationBeforeIdentity = _shell.AppBarSetPosCallCount;
            await ToggleIdentityAsync(a.Id, _views[a.Id]);
            await Task.Delay(100);
            await WaitDocumentAsync(_views[a.Id]);
            var identity = await EvalAsync(
                _views[a.Id],
                "JSON.stringify({agent:navigator.userAgent,mobile:navigator.userAgentData?.mobile,header:window.requestAgent,hint:window.requestMobile,viewport:innerWidth,screen:screen.width,platform:navigator.platform,scrollWidth:document.documentElement.scrollWidth,clientWidth:document.documentElement.clientWidth})"
            );
            using var identityDoc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(identity)!);
            var mobile = GetSetting(a.Id).MobileMode;
            var correctIdentity =
                identityDoc.RootElement.GetProperty("agent").GetString()!.Contains("Mobile") == mobile
                && identityDoc.RootElement.GetProperty("mobile").GetBoolean() == mobile
                && identityDoc.RootElement.GetProperty("header").GetString()!.Contains("Mobile") == mobile
                && identityDoc.RootElement.GetProperty("hint").GetString() == (mobile ? "?1" : "?0");
            results["identityToggleKeepsHostGeometry"] =
                nativeBoundsBeforeIdentity.Equals(
                    new
                    {
                        Left,
                        Top,
                        Width,
                        Height,
                        PanelWidth = _panel.Width,
                        PanelHeight = _panel.Height,
                    }
                )
                && _shell.AppBarSetPosCallCount == reservationBeforeIdentity;
            if (!(bool)results["identityToggleKeepsHostGeometry"]!)
                throw new InvalidOperationException("Identity toggle changed host geometry or reserved space");
            if (!GetSetting(a.Id).MobileMode)
            {
                await ToggleIdentityAsync(a.Id, _views[a.Id]);
                await Task.Delay(100);
                await WaitDocumentAsync(_views[a.Id]);
            }
            async Task<(JsonElement Metrics, long AppBarCallsAfterCommit)> ResizeMetricsAsync(double panelWidth)
            {
                _panel.Width = panelWidth;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                CommitPanelResize();
                var appBarCallsAfterCommit = _shell.AppBarSetPosCallCount;
                await Task.Delay(350);
                var raw = await EvalAsync(
                    _views[a.Id],
                    "JSON.stringify({inner:innerWidth,client:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth})"
                );
                return (
                    JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)!).RootElement.Clone(),
                    appBarCallsAfterCommit
                );
            }
            Stage("mobile-viewport-resize");
            var mobileViewportStart = _mobileViewportUpdates;
            var narrowResize = await ResizeMetricsAsync(360);
            var narrowMetrics = narrowResize.Metrics;
            var narrowUpdateCount = _mobileViewportUpdates;
            var narrowFits =
                narrowMetrics.GetProperty("inner").GetInt32() == _mobileViewports[a.Id].Width
                && narrowMetrics.GetProperty("client").GetInt32() == _mobileViewports[a.Id].Width
                && narrowMetrics.GetProperty("scroll").GetInt32() <= narrowMetrics.GetProperty("client").GetInt32();
            var wideResize = await ResizeMetricsAsync(720);
            var wideMetrics = wideResize.Metrics;
            var wideUpdateCount = _mobileViewportUpdates;
            await Task.Delay(350);
            var resizeLoopStopped = _mobileViewportUpdates == wideUpdateCount;
            var wideFits =
                wideMetrics.GetProperty("inner").GetInt32() == _mobileViewports[a.Id].Width
                && wideMetrics.GetProperty("client").GetInt32() == _mobileViewports[a.Id].Width
                && wideMetrics.GetProperty("scroll").GetInt32() <= wideMetrics.GetProperty("client").GetInt32();
            var noExtraAppBarWrites = _shell.AppBarSetPosCallCount == wideResize.AppBarCallsAfterCommit;
            results["mobileViewportResizeFitsAndSettles"] = new
            {
                narrowFits,
                wideFits,
                updates = _mobileViewportUpdates - mobileViewportStart,
                narrow = narrowMetrics,
                wide = wideMetrics,
                resizeLoopStopped,
                noExtraAppBarWrites,
            };
            if (
                !(
                    narrowFits
                    && wideFits
                    && resizeLoopStopped
                    && noExtraAppBarWrites
                    && _mobileViewportUpdates - mobileViewportStart == 2
                )
            )
                throw new InvalidOperationException("Mobile viewport did not follow a committed panel resize cleanly");
            await _settingsWriter.FlushAsync();
            var readBack = AppSettings.SafeLoad(Path.Combine(_root, "settings.json"));
            results["independentAppZoom"] = independentZoom;
            results["mobileDesktopIdentityMatchesRequestAndClientHints"] = correctIdentity;
            results["viewportMetrics"] = identityDoc.RootElement.Clone();
            results["zoomAndIdentityPersisted"] =
                readBack.Launchers[a.Id].MobileMode == GetSetting(a.Id).MobileMode
                && Math.Abs(readBack.Launchers[a.Id].Zoom - 1.3) < .001
                && Math.Abs(readBack.Launchers[Launchers.All[1].Id].Zoom - .8) < .001;
            if (!independentZoom || !correctIdentity || !(bool)results["zoomAndIdentityPersisted"]!)
                throw new InvalidOperationException("App zoom/identity independence or persistence failed");
            Stage("remaining-launchers");
            foreach (
                var l in Launchers.All.Where(x =>
                    x.Id != a.Id && x.Id != Launchers.All[1].Id && x.Id != Launchers.All[2].Id
                )
            )
            {
                await ActivateAsync(l);
                await WaitDocumentAsync(_views[l.Id]);
            }
            Stage("app-return-into-panel");
            var calendarApp = Launchers.All[3];
            await ActivateAsync(calendarApp, false);
            var oldCalendar = _views[calendarApp.Id];
            var nativeWindowsBefore = System.Windows.Application.Current.Windows.Count;
            var calendarBefore = oldCalendar.CoreWebView2.Source;
            await EvalAsync(
                oldCalendar,
                "window.fixturePopup='';window.fixtureGoogleProxy=window.open('about:blank','calendar-signin');if(window.fixtureGoogleProxy)window.fixtureGoogleProxy.location.href='/google-auth-intent';true"
            );
            var promotionDeadline = DateTime.UtcNow.AddSeconds(10);
            while (_promotedAuthViews == 0 && DateTime.UtcNow < promotionDeadline)
                await Task.Delay(50);
            await WaitDocumentAsync(_views[calendarApp.Id]);
            var authHandoff =
                _promotedAuthViews == 1
                && !ReferenceEquals(_views[calendarApp.Id], oldCalendar)
                && !_panel.HasActiveAuth
                && _fixtureAppReturns == 1
                && calendarBefore.EndsWith("/calendar-b/", StringComparison.Ordinal)
                && !_navigationSessions[calendarApp.Id].AuthenticationActive;
            results["signedInAppPromotedIntoPrimaryPanelWithoutReload"] = authHandoff;
            results["noNativePopupWindowsCreated"] =
                System.Windows.Application.Current.Windows.Count == nativeWindowsBefore;
            await EvalAsync(_views[calendarApp.Id], "window.close();true");
            await Task.Delay(150);
            results["promotedViewCannotCloseHostPanel"] =
                _panel.IsVisible && _views[calendarApp.Id].CoreWebView2 is not null;
            if (!(bool)results["promotedViewCannotCloseHostPanel"]!)
                throw new InvalidOperationException("Promoted browser closed its containing panel");
            if (!authHandoff || !(bool)results["noNativePopupWindowsCreated"]!)
                throw new InvalidOperationException(
                    "Auth return remained in a separate surface or was reloaded/reparented"
                );
            OpenExternal(new Uri("https://outside.example.test/"));
            results["automaticExternalLaunchBlocked"] = _externalLaunchRequests == 0 && _externalSuppressed > 0;
            if (!(bool)results["automaticExternalLaunchBlocked"]!)
                throw new InvalidOperationException("An automatic outside navigation launched the browser");
            Stage("main-cross-host-redirect");
            var mainB = _views[Launchers.All[2].Id];
            mainB.CoreWebView2.Navigate(_fixtureBase + "main-redirect");
            await Task.Delay(200);
            await WaitDocumentAsync(mainB);
            var mainRedirectPassed =
                new Uri(mainB.CoreWebView2.Source).Host == "localhost" && _externalLaunchRequests == 0;
            results["mainCrossHostRedirectStaysEmbedded"] = mainRedirectPassed;
            if (!mainRedirectPassed)
                throw new InvalidOperationException(
                    "Main automatic cross-host redirect escaped to the external browser"
                );
            Stage("hidden-renderer-recovery");
            var recoveryLauncher = Launchers.All.Single(x => x.Id == "soundcloud");
            ClosePanel(false);
            try
            {
                await _views[recoveryLauncher.Id]
                    .CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}")
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception crashError)
            {
                results["crashInvocationException"] = crashError.GetType().Name + ": " + crashError.Message;
            }
            var failureDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!_initializationErrors.ContainsKey(recoveryLauncher.Id) && DateTime.UtcNow < failureDeadline)
                await Task.Delay(50);
            results["hiddenCrashRegistered"] = _initializationErrors.ContainsKey(recoveryLauncher.Id);
            await ActivateAsync(recoveryLauncher, false);
            var hiddenFailureSurfaced = _panel.HasFailure;
            results["hiddenFailureSurfaced"] = hiddenFailureSurfaced;
            await RetryAsync(recoveryLauncher);
            await WaitDocumentAsync(_views[recoveryLauncher.Id]);
            var hiddenFailureRecovered =
                hiddenFailureSurfaced && !_panel.HasFailure && !_initializationErrors.ContainsKey(recoveryLauncher.Id);
            results["hiddenRendererFailureSurfacedAndRecovered"] = hiddenFailureRecovered;
            results["recoveryHasFailure"] = _panel.HasFailure;
            results["recoveryHasStoredError"] = _initializationErrors.ContainsKey(recoveryLauncher.Id);
            if (!hiddenFailureRecovered)
                throw new InvalidOperationException("Hidden renderer failure did not surface a working Retry");
            await RunUnloadRegressionChecksAsync(results);
            if (!_options.BrowserOnly)
            {
                Stage("native-reservation");
                var baseArea = _workAreaBefore ?? RuntimeProbe.CaptureWorkArea();
                var dockArea = RuntimeProbe.CaptureWorkArea();
                var initialMode = GetSetting(a.Id).Mode;
                GetSetting(a.Id).Mode = PanelMode.Split;
                Reserve(_settings.DockWidth, GetPanelWidth(GetSetting(a.Id)));
                var splitArea = RuntimeProbe.CaptureWorkArea();
                var dockReserved = dockArea.Width < baseArea.Width;
                var splitReserved = splitArea.Width < dockArea.Width;
                GetSetting(a.Id).Mode = initialMode;
                if (!dockReserved || !splitReserved)
                    throw new InvalidOperationException("Work-area reservation geometry did not change as expected");
                results["workAreas"] = new
                {
                    before = baseArea,
                    dock = dockArea,
                    split = splitArea,
                };
                await Task.Delay(4000);
                try
                {
                    results["processMetrics"] = await RuntimeProbe.MeasureAsync(await _environment, 5000);
                }
                catch (Exception metricError)
                {
                    results["metricsStatus"] = metricError.GetType().Name;
                }
                HideUtility();
                var releasedArea = RuntimeProbe.CaptureWorkArea();
                var workAreaRestored = releasedArea == baseArea;
                results["workAreaRestored"] = workAreaRestored;
                if (!workAreaRestored)
                    throw new InvalidOperationException("Work area was not restored after hide");
                ToggleVisible();
                var trayRestoreReserved = RuntimeProbe.CaptureWorkArea().Width == dockArea.Width;
                HideUtility();
                results["trayShowAfterHide"] = trayRestoreReserved;
                if (!trayRestoreReserved || RuntimeProbe.CaptureWorkArea() != baseArea)
                    throw new InvalidOperationException("Tray show/hide reservation regression");
            }
            else
            {
                results["nativeReservationChecks"] =
                    "Skipped: browser-only fixture avoids changing the desktop work area while another sidebar is running.";
            }
            results["status"] = "completed";
        }
        catch (Exception e)
        {
            results["status"] = "failed";
            results["error"] = e.GetType().Name;
            results["diagnosticMessage"] = e.Message;
            results["diagnosticStack"] = e.StackTrace;
            try
            {
                HideUtility();
                results["workAreaRestored"] = RuntimeProbe.CaptureWorkArea() == _workAreaBefore;
            }
            catch { }
        }
        results["interactionMetrics"] = _interactionMetrics.ToArray();
        results["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds;
        await File.WriteAllTextAsync(
            Path.Combine(_root, "smoke-evidence.json"),
            JsonSerializer.Serialize<Dictionary<string, object?>>(
                results,
                new JsonSerializerOptions { WriteIndented = true }
            )
        );
        Quit(results["status"]?.ToString() == "completed" ? 0 : 1);
    }

    private async Task RunLayoutRegressionAsync()
    {
        var results = new Dictionary<string, object?> { ["mode"] = "native-layout-regression" };
        try
        {
            var a = Launchers.All[0];
            var b = Launchers.All[1];
            GetSetting(a.Id).Mode = PanelMode.Split;
            GetSetting(a.Id).Width = 430;
            GetSetting(b.Id).Mode = PanelMode.Overlay;
            GetSetting(b.Id).Width = 610;
            await ActivateAsync(a);
            await Task.Delay(350);
            var area = RuntimeProbe.CaptureWorkArea();
            var calls = _shell.AppBarSetPosCallCount;
            var width = _panel.Width;
            for (var i = 0; i < 40; i++)
            {
                await ActivateAsync(i % 2 == 0 ? b : a, false);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            await Task.Delay(200);
            results["switchCount"] = 40;
            results["appBarSetPosDuringSwitches"] = _shell.AppBarSetPosCallCount - calls;
            results["desktopWorkAreaUnchanged"] = RuntimeProbe.CaptureWorkArea() == area;
            results["panelModeAndWidthUnchanged"] = _openMode == PanelMode.Split && _panel.Width == width;
            results["differentSavedModesAndWidthsPreserved"] =
                GetSetting(a.Id).Mode == PanelMode.Split
                && GetSetting(b.Id).Mode == PanelMode.Overlay
                && GetSetting(a.Id).Width == 430
                && GetSetting(b.Id).Width == 610;
            if (
                _shell.AppBarSetPosCallCount != calls
                || RuntimeProbe.CaptureWorkArea() != area
                || _panel.Width != width
                || _openMode != PanelMode.Split
            )
                throw new InvalidOperationException("Tab switching changed the desktop reservation or panel geometry");
            await ActivateAsync(b, false);
            _panel.Width = 610;
            await Dispatcher.Yield(DispatcherPriority.Background);
            CommitPanelResize();
            results["resizeMatchingSavedWidthChangesReservation"] =
                Math.Abs(_openWidth - 610) < 1 && RuntimeProbe.CaptureWorkArea().Width < area.Width;
            if (!(bool)results["resizeMatchingSavedWidthChangesReservation"]!)
                throw new InvalidOperationException(
                    "Resize to another app's already saved width did not update reservation"
                );
            _views[b.Id] = new WebView2();
            OnToolbar("mode");
            await Task.Delay(200);
            results["explicitModeButtonChangesReservation"] =
                _openMode == PanelMode.Overlay && RuntimeProbe.CaptureWorkArea().Width > area.Width;
            if (!(bool)results["explicitModeButtonChangesReservation"]!)
                throw new InvalidOperationException("Explicit mode toggle did not release panel reservation");
            ClosePanel(false);
            await ActivateAsync(b);
            results["freshOpeningUsesSavedPreference"] =
                _openMode == PanelMode.Overlay && Math.Abs(_panel.Width - 610) < 1;
            results["status"] = "completed";
        }
        catch (Exception error)
        {
            results["status"] = "failed";
            results["error"] = error.Message;
        }
        finally
        {
            ClosePanel(false);
            _shell.Release();
        }
        await File.WriteAllTextAsync(
            Path.Combine(_root, "layout-evidence.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
        );
        Quit(results["status"]?.ToString() == "completed" ? 0 : 1);
    }

    private static async Task WaitDocumentAsync(WebView2 view)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (
                    await EvalAsync(view, "document.readyState==='complete' && !!document.querySelector('#input')")
                    == "true"
                )
                    return;
            }
            catch { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Fixture document did not load");
    }

    private static async Task<bool> WaitForValueAsync(WebView2 view, string expression, string expected, int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (JsonSerializer.Deserialize<string?>(await EvalAsync(view, expression)) == expected)
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<string> EvalAsync(WebView2 view, string script)
    {
        if (view.CoreWebView2 is not { } core)
            throw new InvalidOperationException(
                $"Fixture browser detached; loaded={view.IsLoaded}; visible={view.IsVisible}; parent={view.Parent?.GetType().Name}; windowLoaded={Window.GetWindow(view)?.IsLoaded}; windowVisible={Window.GetWindow(view)?.IsVisible}"
            );
        return await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
