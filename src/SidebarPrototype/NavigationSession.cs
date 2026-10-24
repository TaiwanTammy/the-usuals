namespace SidebarPrototype;

public enum NavigationDecision { Embed, External, Block }

/// <summary>Routes a browser flow without exporting its POST data or one-time callbacks to another browser.</summary>
public sealed class NavigationSession
{
    private readonly Launcher launcher;
    private readonly bool ownedPopup;
    private readonly Uri? fixtureOrigin;
    public NavigationSession(Launcher launcher, bool ownedPopup = false, Uri? fixtureOrigin = null) { this.launcher=launcher; this.ownedPopup=ownedPopup; this.fixtureOrigin=fixtureOrigin; AuthenticationActive=ownedPopup; }
    private ulong? _acceptedNavigation;
    public bool AuthenticationActive { get; private set; }
    public NavigationDecision Decide(string? value, ulong navigationId, bool redirected, bool userInitiated)
    {
        if (UrlPolicy.IsPopupBootstrap(value)) return ownedPopup ? NavigationDecision.Embed : NavigationDecision.Block;
        if (!UrlPolicy.TryParseHttp(value, out var uri)) return NavigationDecision.Block;
        var fixtureNetwork = fixtureOrigin is not null && uri!.IsLoopback && uri.Port == fixtureOrigin.Port && uri.Scheme == fixtureOrigin.Scheme;
        var secure = uri!.Scheme == Uri.UriSchemeHttps || fixtureNetwork;
        var known = UrlPolicy.ShouldEmbed(uri, launcher, fixtureOrigin);
        if (ownedPopup) return secure ? NavigationDecision.Embed : NavigationDecision.Block;
        if (known && !secure) return NavigationDecision.Block;
        if (known) {
            _acceptedNavigation = navigationId;
            if (!uri.IdnHost.Equals(launcher.StartUri.IdnHost, StringComparison.OrdinalIgnoreCase) && !(fixtureOrigin is not null && uri.Host==fixtureOrigin.Host && uri.Port==fixtureOrigin.Port)) AuthenticationActive = true;
            return NavigationDecision.Embed;
        }
        // An accepted browser navigation owns its redirects. A sign-in document also
        // owns form submissions and script callbacks until it returns to the app.
        // These pages remain ordinary untrusted web content, with no native grants.
        if (secure && (redirected && _acceptedNavigation == navigationId || AuthenticationActive)) {
            _acceptedNavigation = navigationId; AuthenticationActive = true;
            return NavigationDecision.Embed;
        }
        return redirected ? NavigationDecision.Block : NavigationDecision.External;
    }
    public void Complete(string? source, bool success)
    {
        if (!ownedPopup && success && UrlPolicy.TryParseHttp(source, out var uri) &&
            (uri!.IdnHost.Equals(launcher.StartUri.IdnHost, StringComparison.OrdinalIgnoreCase) || fixtureOrigin is not null && uri.Host==fixtureOrigin.Host && uri.Port==fixtureOrigin.Port)) AuthenticationActive = false;
    }
}
