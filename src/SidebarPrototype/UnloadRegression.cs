using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    // Fixture-only gates let the regression suite hold continuations across an unload.
    internal Func<Task>? UnloadRegressionMainControllerReadyHook { get; set; }
    internal Func<Task>? UnloadRegressionPopupControllerReadyHook { get; set; }
    internal Func<Task>? UnloadRegressionPromotionConfiguredHook { get; set; }
    internal TaskCompletionSource<(bool Handled, bool HasNewWindow)>? UnloadRegressionPopupRequestFinished { get; set; }

    private async Task RunUnloadRegressionChecksAsync(Dictionary<string, object?> results)
    {
        if (!_fixture || _fixtureBase is null) throw new InvalidOperationException("Unload regressions require the local browser fixture.");
        var failures = new List<string>();
        void Check(string key, bool passed, object? detail = null)
        {
            results[key] = passed;
            if (detail is not null) results[key + "Details"] = detail;
            if (!passed) failures.Add(key);
        }

        var gmail = Launchers.All.Single(x => x.Id == "gmail-a");
        var calendar = Launchers.All.Single(x => x.Id == "calendar-a");
        var gmailB = Launchers.All.Single(x => x.Id == "gmail-b");
        var soundcloud = Launchers.All.Single(x => x.Id == "soundcloud");

        // Closing an inactive controller must preserve the visible sibling and profile data.
        await ActivateAsync(gmail, false).WaitAsync(TimeSpan.FromSeconds(20));
        var firstGmail = _views[gmail.Id]; await WaitDocumentAsync(firstGmail);
        var originalZoom = GetSetting(gmail.Id).Zoom; var originalMobile = GetSetting(gmail.Id).MobileMode;
        GetSetting(gmail.Id).Zoom = 1.27; GetSetting(gmail.Id).MobileMode = true; firstGmail.ZoomFactor = 1.27;
        await EvalAsync(firstGmail, "localStorage.setItem('sidebar-unload-marker','account-a-retained');document.cookie='sidebar-unload-cookie=account-a-retained; path=/';true");
        var firstInstance = JsonSerializer.Deserialize<string>(await EvalAsync(firstGmail, "window.instanceId"));
        await ActivateAsync(calendar, false).WaitAsync(TimeSpan.FromSeconds(20));
        var calendarView = _views[calendar.Id]; await WaitDocumentAsync(calendarView);
        var calendarInstance = await EvalAsync(calendarView, "window.instanceId");
        var visibleWidth = _panel.ActualWidth; var visibleHeight = _panel.ActualHeight;
        UnloadLauncher(gmail);
        var inactiveClosed = !_state.IsVisible || _state.ActiveId == calendar.Id;
        var siblingUnchanged = _state.IsVisible && _state.ActiveId == calendar.Id && _panel.IsVisible &&
            Math.Abs(_panel.ActualWidth - visibleWidth) < 1 && Math.Abs(_panel.ActualHeight - visibleHeight) < 1 &&
            _views.TryGetValue(calendar.Id, out var stillCalendar) && ReferenceEquals(calendarView, stillCalendar) &&
            await EvalAsync(calendarView, "window.instanceId") == calendarInstance;
        var noGmailOwnership = !_views.ContainsKey(gmail.Id) && !_viewOwners.ContainsValue(gmail.Id) &&
            !_pendingZoom.Values.Any(x => x.Id == gmail.Id) && !_initializers.ContainsKey(gmail.Id) &&
            !_retiredViews.Any(x => x.LauncherId == gmail.Id) && !_ownedPopups.Any(x => x.LauncherId == gmail.Id);
        var profileMarker = await EvalAsync(calendarView, "localStorage.getItem('sidebar-unload-marker')");
        var profileCookie = await EvalAsync(calendarView, "document.cookie");
        Check("unloadInactivePreservesVisibleSibling", inactiveClosed && siblingUnchanged,
            new { active = _state.ActiveId, visible = _state.IsVisible, visibleWidth, actualWidth = _panel.ActualWidth, visibleHeight, actualHeight = _panel.ActualHeight });
        Check("unloadInactiveReleasesAppOwnership", noGmailOwnership);
        Check("unloadPreservesSharedProfileData", JsonSerializer.Deserialize<string>(profileMarker) == "account-a-retained" && profileCookie.Contains("sidebar-unload-cookie=account-a-retained", StringComparison.Ordinal), new { marker = JsonSerializer.Deserialize<string>(profileMarker), profileCookie });
        await ActivateAsync(gmail, false).WaitAsync(TimeSpan.FromSeconds(20));
        var reopenedGmail = _views[gmail.Id]; await WaitDocumentAsync(reopenedGmail);
        var reopenedInstance = JsonSerializer.Deserialize<string>(await EvalAsync(reopenedGmail, "window.instanceId"));
        var mobileAgent = await EvalAsync(reopenedGmail, "navigator.userAgent.includes('Mobile')");
        var retainedPreferences = Math.Abs(GetSetting(gmail.Id).Zoom - 1.27) < .001 && GetSetting(gmail.Id).MobileMode &&
            Math.Abs(reopenedGmail.ZoomFactor - 1.27) < .001 && mobileAgent == "true";
        Check("unloadReopenCreatesFreshControllerAndPage", !ReferenceEquals(firstGmail, reopenedGmail) && firstInstance != reopenedInstance,
            new { oldInstance = firstInstance, newInstance = reopenedInstance });
        Check("unloadReopenRetainsZoomAndMobileSettings", retainedPreferences,
            new { configuredZoom = GetSetting(gmail.Id).Zoom, actualZoom = reopenedGmail.ZoomFactor, mobile = GetSetting(gmail.Id).MobileMode, mobileAgent });
        GetSetting(gmail.Id).Zoom = originalZoom; GetSetting(gmail.Id).MobileMode = originalMobile;

        // Closing SoundCloud disposes its live, muted audio element with its controller.
        await ActivateAsync(soundcloud, false).WaitAsync(TimeSpan.FromSeconds(20));
        var oldSound = _views[soundcloud.Id]; await WaitDocumentAsync(oldSound);
        oldSound.CoreWebView2!.IsMuted = true;
        await EvalAsync(oldSound, "window.fixtureAudioReady='pending';document.querySelector('#audio').play().then(()=>window.fixtureAudioReady='started',()=>window.fixtureAudioReady='failed');true");
        var audioStarted = await WaitForValueAsync(oldSound, "window.fixtureAudioReady", "started", 5000);
        var audioProgressed = await WaitForValueAsync(oldSound, "String(document.querySelector('#audio').currentTime>0)", "true", 5000);
        UnloadLauncher(soundcloud);
        var soundReleased = !_views.ContainsKey(soundcloud.Id) && !_viewOwners.ContainsValue(soundcloud.Id) &&
            !_retiredViews.Any(x => x.LauncherId == soundcloud.Id) && !_ownedPopups.Any(x => x.LauncherId == soundcloud.Id) &&
            !_pendingZoom.Values.Any(x => x.Id == soundcloud.Id) && !_panel.OwnsView(oldSound) && !_panel.IsVisible;
        var oldSoundAudioStopped = true;
        try { oldSoundAudioStopped = oldSound.CoreWebView2?.IsDocumentPlayingAudio != true; }
        catch (Exception error) when (error is ObjectDisposedException or System.Runtime.InteropServices.COMException) { oldSoundAudioStopped = true; }
        soundReleased &= oldSoundAudioStopped;
        Check("unloadSoundCloudStopsAndReleasesController", audioStarted && audioProgressed && soundReleased,
            new { audioStarted, audioProgressed, released = soundReleased, oldAudioStopped = oldSoundAudioStopped, panelVisible = _panel.IsVisible });
        await ActivateAsync(soundcloud, false).WaitAsync(TimeSpan.FromSeconds(20));
        var newSound = _views[soundcloud.Id]; await WaitDocumentAsync(newSound);
        var reopenedAudioJson = await EvalAsync(newSound, "JSON.stringify({paused:document.querySelector('#audio').paused,time:document.querySelector('#audio').currentTime,instance:window.instanceId})");
        var reopenedAudio = JsonSerializer.Deserialize<string>(reopenedAudioJson) ?? "{}";
        using (var audioState = JsonDocument.Parse(reopenedAudio))
            Check("unloadSoundCloudReopensFreshWithoutAudio", !ReferenceEquals(oldSound, newSound) && audioState.RootElement.GetProperty("paused").GetBoolean() && audioState.RootElement.GetProperty("time").GetDouble() < .25,
                audioState.RootElement.Clone());

        // Hold an initialization continuation, close it while inactive, then reopen the same app.
        UnloadLauncher(gmailB);
        var initEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initGateUsed = 0;
        UnloadRegressionMainControllerReadyHook = async () =>
        {
            if (Interlocked.Exchange(ref initGateUsed, 1) != 0) return;
            initEntered.TrySetResult(); await initRelease.Task.WaitAsync(TimeSpan.FromSeconds(15));
        };
        var staleActivation = ActivateAsync(gmailB, false);
        await initEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var staleInitView = _views[gmailB.Id];
        await ActivateAsync(calendar, false).WaitAsync(TimeSpan.FromSeconds(20));
        var siblingBeforeInitClose = _views[calendar.Id];
        UnloadLauncher(gmailB);
        var initReleasedFromMaps = !_views.ContainsKey(gmailB.Id) && !_initializers.ContainsKey(gmailB.Id) && !_initializationErrors.ContainsKey(gmailB.Id);
        var staleInitViewReleased = !_panel.OwnsView(staleInitView);
        await ActivateAsync(gmailB, false).WaitAsync(TimeSpan.FromSeconds(20));
        var currentAfterInitReopen = _views[gmailB.Id]; await WaitDocumentAsync(currentAfterInitReopen);
        initRelease.TrySetResult(); await staleActivation.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(250);
        var siblingAfterInit = _views.GetValueOrDefault(calendar.Id);
        var initNoResurrection = ReferenceEquals(_views[gmailB.Id], currentAfterInitReopen) && !ReferenceEquals(staleInitView, currentAfterInitReopen) &&
            !_panel.OwnsView(staleInitView) && !_initializationErrors.ContainsKey(gmailB.Id) && ReferenceEquals(siblingBeforeInitClose, siblingAfterInit);
        UnloadRegressionMainControllerReadyHook = null;
        Check("unloadPendingInitializationCannotResurrect", initReleasedFromMaps && initNoResurrection,
            new { releasedFromMaps = initReleasedFromMaps, staleInitViewReleased, staleAndCurrentDiffer = !ReferenceEquals(staleInitView, currentAfterInitReopen), currentSurvived = ReferenceEquals(_views[gmailB.Id], currentAfterInitReopen), staleError = _initializationErrors.GetValueOrDefault(gmailB.Id) });

        // Hold an owned popup initialization across unload; the late continuation must remain orphaned.
        await ActivateAsync(gmail, false).WaitAsync(TimeSpan.FromSeconds(20));
        var popupParent = _views[gmail.Id]; await WaitDocumentAsync(popupParent);
        var popupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var popupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var popupGateUsed = 0;
        UnloadRegressionPopupControllerReadyHook = async () =>
        {
            if (Interlocked.Exchange(ref popupGateUsed, 1) != 0) return;
            popupEntered.TrySetResult(); await popupRelease.Task.WaitAsync(TimeSpan.FromSeconds(15));
        };
        var popupRequestFinished = new TaskCompletionSource<(bool Handled, bool HasNewWindow)>(TaskCreationOptions.RunContinuationsAsynchronously);
        UnloadRegressionPopupRequestFinished = popupRequestFinished;
        var pendingPopupScript = EvalAsync(popupParent, "window.fixturePopup='';window.fixturePendingProxy=window.open('about:blank','pending-unload-popup');if(window.fixturePendingProxy)window.fixturePendingProxy.location.href='/popup';true");
        await popupEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var calendarBeforePopupUnload = _views[calendar.Id];
        await ActivateAsync(calendar, false).WaitAsync(TimeSpan.FromSeconds(20));
        var pendingOwned = _ownedPopups.Where(x => x.LauncherId == gmail.Id).DistinctBy(x => x.View).ToArray();
        var pendingPopupControllerReady = pendingOwned.Length > 0 && pendingOwned.All(x => x.View.CoreWebView2 is not null);
        UnloadLauncher(gmail);
        var popupEntriesReleased = !_ownedPopups.Any(x => x.LauncherId == gmail.Id) && !_views.ContainsKey(gmail.Id) && pendingOwned.Length > 0 &&
            pendingOwned.All(x => x.Window.IsClosed && !_panel.OwnsPopup(x.Window) && !_panel.OwnsView(x.View));
        popupRelease.TrySetResult();
        var popupRequestResult = await popupRequestFinished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var popupScriptResult = await pendingPopupScript.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(150);
        var calendarAfterPopup = _views.GetValueOrDefault(calendar.Id);
        var popupNoResurrection = !_views.ContainsKey(gmail.Id) && !_ownedPopups.Any(x => x.LauncherId == gmail.Id) &&
            !_initializationErrors.ContainsKey(gmail.Id) && pendingOwned.All(x => !_panel.OwnsPopup(x.Window) && !_panel.OwnsView(x.View)) &&
            ReferenceEquals(calendarBeforePopupUnload, calendarAfterPopup);
        UnloadRegressionPopupControllerReadyHook = null;
        UnloadRegressionPopupRequestFinished = null;
        Check("unloadPendingPopupCannotResurrect", pendingPopupControllerReady && popupEntriesReleased && popupNoResurrection && popupRequestResult.Handled && !popupRequestResult.HasNewWindow,
            new { pendingPopupControllerReady, entriesReleased = popupEntriesReleased, noResurrection = popupNoResurrection, requestHandled = popupRequestResult.Handled, newWindowAssigned = popupRequestResult.HasNewWindow, popupScriptResult, pendingPopupCount = pendingOwned.Length, staleError = _initializationErrors.GetValueOrDefault(gmail.Id) });

        // Hold a just-configured promotion, unload its app and popup, then release its stale continuation.
        await ActivateAsync(gmail, false).WaitAsync(TimeSpan.FromSeconds(20));
        var prePromotion = _views[gmail.Id]; await WaitDocumentAsync(prePromotion);
        var promotedCountBefore = _promotedAuthViews;
        var promotionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promotionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UnloadRegressionPromotionConfiguredHook = async () =>
        {
            promotionEntered.TrySetResult(); await promotionRelease.Task.WaitAsync(TimeSpan.FromSeconds(15));
        };
        await EvalAsync(prePromotion, "window.fixturePopup='';window.open('/google-auth-intent','unload-promotion');true");
        var promotionDeadline = DateTime.UtcNow.AddSeconds(12);
        while (_promotedAuthViews == promotedCountBefore && DateTime.UtcNow < promotionDeadline) await Task.Delay(50);
        var promotionOccurred = _promotedAuthViews > promotedCountBefore && _views.TryGetValue(gmail.Id, out var promoted) && !ReferenceEquals(promoted, prePromotion) && _retiredViews.Any(x => x.LauncherId == gmail.Id && ReferenceEquals(x.View, prePromotion));
        if (promotionOccurred)
        {
            await promotionEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var promotedView = _views[gmail.Id];
            await EvalAsync(promotedView, "window.open('/gmail-a/','unload-owned-popup');true");
            var popupDeadline = DateTime.UtcNow.AddSeconds(8);
            while ((!_ownedPopups.Any(x => x.LauncherId == gmail.Id) || _ownedPopups.Where(x => x.LauncherId == gmail.Id).Any(x => x.View.CoreWebView2 is null)) && DateTime.UtcNow < popupDeadline) await Task.Delay(50);
            var promotionPopups = _ownedPopups.Where(x => x.LauncherId == gmail.Id).DistinctBy(x => x.View).ToArray();
            foreach (var popup in promotionPopups) await WaitDocumentAsync(popup.View);
            var promotionPopupWasReady = promotionPopups.Length > 0 && promotionPopups.All(x => x.View.CoreWebView2 is not null && _panel.OwnsPopup(x.Window));
            UnloadLauncher(gmail);
            var promotionReleased = !_views.ContainsKey(gmail.Id) && !_retiredViews.Any(x => x.LauncherId == gmail.Id) && !_ownedPopups.Any(x => x.LauncherId == gmail.Id) &&
                !_panel.OwnsView(prePromotion) && !_panel.OwnsView(promotedView) && promotionPopups.All(x => x.Window.IsClosed && !_panel.OwnsPopup(x.Window) && !_panel.OwnsView(x.View)) &&
                ReferenceEquals(calendarBeforePopupUnload, _views.GetValueOrDefault(calendar.Id));
            promotionRelease.TrySetResult(); await Task.Delay(300);
            var promotionNoResurrection = !_views.ContainsKey(gmail.Id) && !_retiredViews.Any(x => x.LauncherId == gmail.Id) && !_ownedPopups.Any(x => x.LauncherId == gmail.Id) && !_initializationErrors.ContainsKey(gmail.Id) &&
                ReferenceEquals(calendarBeforePopupUnload, _views.GetValueOrDefault(calendar.Id));
            Check("unloadPendingPromotionCannotResurrect", promotionReleased && promotionNoResurrection,
                new { released = promotionReleased, noResurrection = promotionNoResurrection, staleError = _initializationErrors.GetValueOrDefault(gmail.Id) });
            Check("unloadPromotedAppReleasesOnlyOwnedViews", promotionPopupWasReady && promotionReleased,
                new { retiredReleased = !_panel.OwnsView(prePromotion), promotedReleased = !_panel.OwnsView(promotedView), ownedPopupCount = promotionPopups.Length, promotionPopupWasReady, siblingKept = _views.ContainsKey(calendar.Id) });
            UnloadRegressionPromotionConfiguredHook = null;
        }
        else {
            promotionRelease.TrySetResult(); UnloadRegressionPromotionConfiguredHook = null;
            Check("unloadPendingPromotionCannotResurrect", false, new { promotionOccurred, promotedCount = _promotedAuthViews });
            Check("unloadPromotedAppReleasesOnlyOwnedViews", false, new { promotionOccurred, promotedCount = _promotedAuthViews });
        }

        if (failures.Count > 0) throw new InvalidOperationException("Unload regression checks failed: " + string.Join(", ", failures));
    }
}
