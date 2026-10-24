namespace SidebarPrototype;

public static class AppReturnPolicy
{
    public static bool IsGoogle(Launcher app) => app.Profile is "AccountA" or "AccountB";
    public static bool IsGoogleLanding(Launcher app,string? source) => IsGoogle(app) && UrlPolicy.TryParseHttp(source,out var uri) && uri!.Scheme=="https" && uri.IdnHost=="workspace.google.com";
    public static bool IsGoogleSignInIntent(Uri uri)
    {
        if(uri.Scheme!=Uri.UriSchemeHttps || !uri.IdnHost.Equals("accounts.google.com",StringComparison.OrdinalIgnoreCase)) return false;
        var path=uri.AbsolutePath;
        return path.Equals("/",StringComparison.Ordinal) ||
            path.Equals("/AccountChooser",StringComparison.OrdinalIgnoreCase) || path.StartsWith("/AccountChooser/",StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/signin",StringComparison.OrdinalIgnoreCase) || path.StartsWith("/signin/",StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/o/oauth2/",StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v3/signin/",StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/gsi/",StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/ServiceLogin",StringComparison.OrdinalIgnoreCase) || path.StartsWith("/ServiceLogin/",StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsAppReturn(Launcher app,string? source,bool waitingForSignIn,Uri? fixture = null)
    {
        if(!waitingForSignIn || !IsGoogle(app) || !UrlPolicy.TryParseHttp(source,out var uri)) return false;
        if(fixture is not null) return uri!.Host==fixture.Host && uri.Port==fixture.Port && uri.AbsolutePath=="/app-return";
        return uri!.Scheme=="https" && uri.IdnHost.Equals(app.StartUri.IdnHost,StringComparison.OrdinalIgnoreCase) && UrlPolicy.IsAllowedRestore(source,app);
    }
}
