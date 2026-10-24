using System.Text.Json;
using System.Text.Json.Serialization;

namespace SidebarPrototype;

public enum PanelMode { Overlay, Split }
public enum SiteTheme { Dark, Light }
public sealed record Launcher(string Id, string Label, string Profile, Uri StartUri);
public sealed class LauncherSetting
{
    public PanelMode Mode { get; set; } = PanelMode.Overlay;
    public double Width { get; set; }
    public double Zoom { get; set; } = 1;
    public SiteTheme Theme { get; set; } = SiteTheme.Dark;
    public bool MobileMode { get; set; }
    public string? LastUrl { get; set; }
}
public sealed class AppSettings
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public double DockWidth { get; set; } = 45;
    public bool Topmost { get; set; } = true;
    public uint ShortcutModifiers { get; set; } = 0x0001 | 0x0002 | 0x4000;
    public uint ShortcutVirtualKey { get; set; } = 0x20;
    public BrandingMode BrandingMode { get; set; } = SidebarPrototype.BrandingMode.Rotate;
    public int LastBrandIndex { get; set; } = -1;
    public Dictionary<string, LauncherSetting> Launchers { get; set; } = [];
    public List<CustomLauncherDefinition> CustomLaunchers { get; set; } = [];
    public List<string> LauncherOrder { get; set; } = [];
    public static AppSettings SafeLoad(string file)
    {
        try { if (File.Exists(file)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions())?.Validate() ?? new(); }
        catch { try { File.Copy(file, file + ".corrupt.bak", true); } catch { } }
        return new();
    }
    public AppSettings Validate()
    {
        DockWidth = double.IsFinite(DockWidth) ? Math.Clamp(DockWidth, 36, 240) : 45;
        if (ShortcutVirtualKey == 0 || (ShortcutModifiers & 0x000F) == 0) { ShortcutModifiers = 0x0001 | 0x0002 | 0x4000; ShortcutVirtualKey = 0x20; }
        if (!Enum.IsDefined<SidebarPrototype.BrandingMode>(BrandingMode)) BrandingMode = SidebarPrototype.BrandingMode.Rotate;
        if (LastBrandIndex is not (-1 or 0 or 1)) LastBrandIndex = -1;
        Launchers ??= [];
        CustomLaunchers ??= [];
        LauncherOrder ??= [];
        CustomLaunchers = CustomLaunchers.Where(x => x is not null && LauncherCatalog.IsValidDefinition(x)).GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).ToList();
        LauncherOrder = LauncherOrder.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var x in CustomLaunchers) if (x.IconPath is { } icon && (Path.GetFileName(icon) != icon || icon.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) x.IconPath = null;
        foreach (var key in Launchers.Keys.ToArray()) {
            var s = Launchers[key] ?? new();
            s.Width = !double.IsFinite(s.Width) || s.Width < 0 ? 320 : s.Width == 0 ? 0 : Math.Clamp(s.Width, 320, 2000);
            s.Zoom = double.IsFinite(s.Zoom) ? Math.Clamp(s.Zoom, .5, 2) : 1;
            if (!Enum.IsDefined(s.Mode)) s.Mode = PanelMode.Overlay;
            if (!Enum.IsDefined(s.Theme)) s.Theme = SiteTheme.Dark;
            s.LastUrl = UrlPolicy.IsSafeRestorable(s.LastUrl) ? s.LastUrl : null;
            Launchers[key] = s;
        }
        return this;
    }
    public async Task SaveAtomicAsync(string file, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct);
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this, JsonOptions()), ct);
            try { if (File.Exists(file)) File.Replace(temp, file, file + ".bak", true); else File.Move(temp, file); }
            finally { try { File.Delete(temp); } catch { } }
        } finally { _saveGate.Release(); }
    }
    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}

public sealed class CustomLauncherDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public string Profile { get; set; } = "";
    public string? IconPath { get; set; }
}

public static class LauncherCatalog
{
    public static bool IsValidDefinition(CustomLauncherDefinition x) =>
        !string.IsNullOrWhiteSpace(x.Id) && x.Id.StartsWith("custom-", StringComparison.Ordinal) && x.Id.Length is > 7 and <= 48 &&
        !string.IsNullOrWhiteSpace(x.Label) && x.Label.Trim().Length <= 48 && !string.IsNullOrWhiteSpace(x.Profile) &&
        x.Profile == "Custom-" + x.Id[7..] && !string.IsNullOrWhiteSpace(x.Url) && UrlPolicy.TryParseHttp(x.Url, out _);

    public static string NormalizeUrl(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) throw new FormatException("Enter a website address.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!UrlPolicy.TryParseHttp(text, out var parsed)) throw new FormatException("Use an http or https address without a username or password.");
        var builder = new UriBuilder(parsed!) { Host = parsed!.IdnHost.ToLowerInvariant() };
        return builder.Uri.AbsoluteUri;
    }

    public static Launcher ToLauncher(CustomLauncherDefinition x) => new(x.Id, x.Label.Trim(), x.Profile, new Uri(x.Url));

    public static IReadOnlyList<Launcher> For(AppSettings settings)
    {
        var all=Launchers.All.Concat(settings.CustomLaunchers.Where(IsValidDefinition).Select(ToLauncher)).ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var order=settings.LauncherOrder.Where(id=>!string.IsNullOrWhiteSpace(id)&&all.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        order.AddRange(all.Keys.Where(id=>!order.Contains(id,StringComparer.Ordinal)));
        return order.Select(id=>all[id]).ToArray();
    }
}

public static class UrlPolicy
{
    private static readonly string[] GoogleAuthHosts = ["accounts.google.com", "myaccount.google.com"];
    private static readonly string[] GoogleCompletionHosts = ["www.google.com"];
    public static bool TryParseHttp(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.UserInfo.Length != 0 ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(parsed.Host)) return false;
        uri = parsed; return true;
    }
    public static bool IsInternal(Uri uri, Launcher launcher) => uri.IdnHost.Equals(launcher.StartUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
        (launcher.Profile == "SoundCloud" && uri.Scheme == Uri.UriSchemeHttps && uri.IdnHost.Equals("secure.soundcloud.com", StringComparison.OrdinalIgnoreCase)) ||
        ((launcher.Profile is "AccountA" or "AccountB") && IsGoogleAccountRoute(uri));

    private static bool IsGoogleAccountRoute(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (GoogleAuthHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) ||
         (GoogleCompletionHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) &&
          uri.AbsolutePath.StartsWith("/accounts/", StringComparison.OrdinalIgnoreCase)));

    private static bool IsWorkspaceGmailLanding(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IdnHost.Equals("workspace.google.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[0].Equals("gmail", StringComparison.OrdinalIgnoreCase) ||
            segments.Length > 2 && segments[0].Equals("intl", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("gmail", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGoogleAuthPopup(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (GoogleAuthHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) ||
         GoogleCompletionHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) &&
         uri.AbsolutePath.StartsWith("/accounts/", StringComparison.OrdinalIgnoreCase));

    public static bool IsPopupBootstrap(string? value) => string.IsNullOrWhiteSpace(value) ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
    public static bool IsSafeRestorable(string? value)
    {
        if (!TryParseHttp(value, out var u) || u!.Scheme != Uri.UriSchemeHttps) return false;
        static string Decode(string text) { for (var i = 0; i < 3; i++) { var next = Uri.UnescapeDataString(text); if (next == text) break; text = next; } return text; }
        var query = Decode(u.Query); var fragment = Decode(u.Fragment);
        return !query.Contains("code=", StringComparison.OrdinalIgnoreCase) && !query.Contains("token", StringComparison.OrdinalIgnoreCase) &&
            !fragment.Contains("access_token", StringComparison.OrdinalIgnoreCase) && !fragment.Contains("id_token", StringComparison.OrdinalIgnoreCase) && !fragment.Contains("state=", StringComparison.OrdinalIgnoreCase) &&
            !u.AbsolutePath.Contains("oauth", StringComparison.OrdinalIgnoreCase) && !u.AbsolutePath.Contains("callback", StringComparison.OrdinalIgnoreCase);
    }
    public static bool ShouldEmbed(Uri uri, Launcher launcher, Uri? fixtureOrigin = null) => IsInternal(uri, launcher) ||
        launcher.Id.StartsWith("gmail-", StringComparison.OrdinalIgnoreCase) && IsWorkspaceGmailLanding(uri) ||
        (fixtureOrigin is not null && uri.Host == fixtureOrigin.Host && uri.Port == fixtureOrigin.Port);

    public static bool ShouldEmbedPopup(Uri uri, Launcher launcher, Uri? fixtureOrigin = null)
    {
        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) && uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (fixtureOrigin is not null && uri.Host == fixtureOrigin.Host && uri.Port == fixtureOrigin.Port && uri.Scheme == fixtureOrigin.Scheme) return true;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return ShouldEmbed(uri, launcher) || launcher.Profile == "SoundCloud" && IsGoogleAuthPopup(uri);
    }
    public static bool IsAllowedRestore(string? value, Launcher launcher) => IsSafeRestorable(value) && Uri.TryCreate(value, UriKind.Absolute, out var u) &&
        u.IdnHost.Equals(launcher.StartUri.IdnHost, StringComparison.OrdinalIgnoreCase);
}

public static class PanelSizing
{
    public static double Initial(double usableWidth) => Clamp(usableWidth / 3, usableWidth, 45);
    public static double Clamp(double requested, double usableWidth, double dockWidth)
    {
        var available = Math.Max(1, usableWidth - dockWidth);
        var max = Math.Max(1, usableWidth - dockWidth - Math.Min(320, Math.Max(1, available / 2)));
        var min = Math.Min(320, max);
        return Math.Clamp(double.IsFinite(requested) ? requested : min, min, Math.Max(min, max));
    }
}

public sealed class ViewState
{
    public string? ActiveId { get; private set; }
    public bool IsVisible { get; private set; }
    public long Generation { get; private set; }
    public long BeginSwitch(string id) { ActiveId = id; IsVisible = true; return ++Generation; }
    public void Hide() { IsVisible = false; ++Generation; }
    public bool CanPresent(string id, long generation) => IsVisible && ActiveId == id && Generation == generation;
}

public static class Launchers
{
    public static readonly Launcher[] All = [
        new("gmail-a", "Gmail · A", "AccountA", new("https://mail.google.com/mail/u/0/")),
        new("calendar-a", "Calendar · A", "AccountA", new("https://calendar.google.com/calendar/u/0/r")),
        new("gmail-b", "Gmail · B", "AccountB", new("https://mail.google.com/mail/u/0/")),
        new("calendar-b", "Calendar · B", "AccountB", new("https://calendar.google.com/calendar/u/0/r")),
        new("soundcloud", "SoundCloud", "SoundCloud", new("https://soundcloud.com/"))
    ];
}
