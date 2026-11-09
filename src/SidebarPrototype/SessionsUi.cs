using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Cursors = System.Windows.Input.Cursors;

namespace SidebarPrototype;

// The Sessions button above the gear: a list of the Chrome sessions from the Sessions extension. Click one and
// Chrome switches to it. The extension can also hand a tab over to become an app in the dock.
internal sealed partial class HostWindow
{
    private readonly record struct ChromeSession(string Id, string Name, long OpenedAt, string[] Hosts);

    private ChromeBridge? _chrome;
    private List<ChromeSession> _chromeSessions = [];
    private string? _chromeCurrentId;
    private readonly Dictionary<string, ImageSource> _chromeIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _sessionsList = new();
    private Popup? _sessionsPopup;

    private static readonly SolidColorBrush SessionText = Frozen(Color.FromRgb(237, 237, 240));
    private static readonly SolidColorBrush SessionMuted = Frozen(Color.FromRgb(154, 154, 162));
    private static readonly SolidColorBrush SessionAccent = Frozen(Color.FromRgb(122, 167, 255));
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    private void StartChromeBridge()
    {
        if (_options.Fixture) return; // tests never touch the real browser setup
        _ = Task.Run(() => { try { ChromeBridge.Register(_root); } catch { } });
        _chrome = new ChromeBridge();
        _chrome.ConnectionChanged += () => Dispatcher.InvokeAsync(() =>
        {
            if (_chrome.IsConnected) _chrome.Send(new { type = "hello", name = _brandName });
            else { _chromeSessions = []; _chromeCurrentId = null; }
            if (_sessionsPopup?.IsOpen == true) RenderSessions();
        });
        _chrome.MessageReceived += m => Dispatcher.InvokeAsync(() => OnChromeMessage(m));
        _chrome.Start();
    }

    private void AddSessionsButton()
    {
        var button = new WpfButton { Content = new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = Brushes.LightGray }, Width = 36, Height = 36, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Style = FlatButtonStyle, ToolTip = "Chrome sessions", Tag = "sessions" };
        AutomationProperties.SetName(button, "Chrome sessions");
        _sessionsPopup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Custom, CustomPopupPlacementCallback = BesideButtonGrowingUp, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Width = 400, Background = BrushFor(30), BorderBrush = BrushFor(72), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6),
                Child = new ScrollViewer { MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _sessionsList },
            },
        };
        button.Click += (_, _) =>
        {
            if (_chrome is { IsConnected: false }) _ = Task.Run(() => { try { ChromeBridge.Register(_root); } catch { } });
            RenderSessions();
            _sessionsPopup.IsOpen = !_sessionsPopup.IsOpen;
        };
        _bottomButtons.Children.Add(button);
    }

    private void OnChromeMessage(JsonElement m)
    {
        switch (m.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "sessions":
                if (m.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
                    foreach (var icon in icons.EnumerateObject())
                        if (DecodeIcon(icon.Value.GetString()) is { } image) _chromeIcons[icon.Name] = image;
                _chromeCurrentId = m.TryGetProperty("currentId", out var cur) ? cur.GetString() : null;
                _chromeSessions = m.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(s => new ChromeSession(
                        s.GetProperty("id").GetString() ?? "",
                        s.GetProperty("name").GetString() ?? "",
                        s.TryGetProperty("openedAt", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0,
                        s.TryGetProperty("hosts", out var h) && h.ValueKind == JsonValueKind.Array ? h.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [])).ToList()
                    : [];
                if (_sessionsPopup?.IsOpen == true) RenderSessions();
                break;
            case "addApp":
                _ = AddAppFromChromeAsync(Str(m, "url"), Str(m, "name"), Str(m, "icon"));
                break;
        }
        static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    }

    private static ImageSource? DecodeIcon(string? dataUrl)
    {
        try
        {
            if (dataUrl is null || dataUrl.IndexOf("base64,", StringComparison.Ordinal) is var at && at < 0) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(dataUrl[(at + 7)..]));
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private void RenderSessions()
    {
        _sessionsList.Children.Clear();
        string? note = _chrome is not { IsConnected: true } ? "Open Chrome to see your sessions here." : _chromeSessions.Count == 0 ? "No sessions yet." : null;
        if (note is not null)
        {
            _sessionsList.Children.Add(new TextBlock { Text = note, Foreground = SessionMuted, FontSize = 12, Margin = new Thickness(8, 8, 8, 8), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var s in _chromeSessions) _sessionsList.Children.Add(SessionRow(s));
    }

    private const int StripMax = 11;

    private Border SessionRow(ChromeSession s)
    {
        var current = s.Id == _chromeCurrentId;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var name = new TextBlock { Text = s.Name, Foreground = SessionText, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        grid.Children.Add(name);

        // One tiny icon per tab, so how full a session is shows at a glance.
        var strip = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ToolTip = s.Hosts.Length == 1 ? "1 tab" : $"{s.Hosts.Length} tabs" };
        var shown = s.Hosts.Length > StripMax ? StripMax - 1 : s.Hosts.Length;
        foreach (var host in s.Hosts.Take(shown))
            strip.Children.Add(_chromeIcons.TryGetValue(host, out var img)
                ? new Image { Source = img, Width = 8, Height = 8, Margin = new Thickness(0, 0, 2, 0) }
                : new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = BrushFor(80), Margin = new Thickness(0, 0, 2, 0) });
        if (s.Hosts.Length > shown)
            strip.Children.Add(new TextBlock { Text = $"+{s.Hosts.Length - shown}", Foreground = SessionMuted, FontSize = 10, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(strip, 1);
        grid.Children.Add(strip);

        var when = new TextBlock { Text = current ? "Open" : ShortAgo(s.OpenedAt), Foreground = current ? SessionAccent : SessionMuted, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        var row = new Border { Child = grid, Height = 30, Padding = new Thickness(10, 0, 8, 0), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = s.Name.Length > 28 ? s.Name : null };
        row.MouseEnter += (_, _) => row.Background = BrushFor(48);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => SwitchChromeSession(s.Id);
        return row;
    }

    private static string ShortAgo(long openedAt)
    {
        if (openedAt <= 0) return "–";
        var m = (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(openedAt)).TotalMinutes;
        if (m < 1) return "now";
        if (m < 60) return $"{Math.Round(m)}m";
        var h = Math.Round(m / 60);
        return h < 24 ? $"{h}h" : $"{Math.Round(h / 24)}d";
    }

    private void SwitchChromeSession(string id)
    {
        if (_sessionsPopup is not null) _sessionsPopup.IsOpen = false;
        ClosePanel(false);
        AllowSetForegroundWindow(-1); // lets Chrome bring its window to the front
        _chrome?.Send(new { type = "switch", id });
    }

    // A tab from Chrome becomes an app in the dock, then opens so you can sign in to it here.
    private async Task AddAppFromChromeAsync(string url, string name, string iconDataUrl)
    {
        string normalized;
        try { normalized = LauncherCatalog.NormalizeUrl(url); } catch { return; }
        var label = string.IsNullOrWhiteSpace(name) ? new Uri(normalized).Host : name.Trim();
        if (label.Length > 48) label = label[..48].Trim();

        var same = _settings.CustomLaunchers.FirstOrDefault(x => x.Url.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (same is null)
        {
            string? icon = null;
            var at = iconDataUrl.IndexOf("base64,", StringComparison.Ordinal);
            if (at >= 0)
                try
                {
                    var dir = Path.Combine(_root, "customsitefiles");
                    Directory.CreateDirectory(dir);
                    icon = Path.Combine(dir, "from-chrome-" + Guid.NewGuid().ToString("N") + ".png");
                    await File.WriteAllBytesAsync(icon, Convert.FromBase64String(iconDataUrl[(at + 7)..]));
                }
                catch { icon = null; }
            var added = TryAddLauncher(label, normalized, icon, out _);
            if (icon is not null) try { File.Delete(icon); } catch { }
            if (!added) return;
        }
        var id = same?.Id ?? ConfiguredLaunchers.Last().Id;
        if (ConfiguredLaunchers.FirstOrDefault(x => x.Id == id) is not { } launcher) return;
        if (!IsVisible) ToggleVisible();
        await ActivateFromDockAsync(launcher);
    }

#if TEST_HARNESS
    // Made-up sessions, so the UI test can picture the panel without Chrome.
    internal object? CaptureSessionsPanel(string path)
    {
        var day = 86_400_000L; var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _chromeSessions = [
            new("a", "Bayfillers OS", now, ["github.com", "vercel.com"]),
            new("b", "Garage website redesign", now - 3 * 3_600_000, ["github.com", "figma.com", "vercel.com"]),
            new("c", "Taxes 2026", now - day, ["irs.gov", "docs.google.com"]),
            new("d", "A really long session name that will not fit in the row", now - 12 * day, ["example.com"]),
            new("e", "Vacation Bali", now - 40 * day, Enumerable.Range(0, 23).Select(i => $"site{i}.com").ToArray()),
        ];
        _chromeCurrentId = "a";
        var connected = _chrome; _chrome = null;
        _sessionsList.Children.Clear();
        foreach (var s in _chromeSessions) _sessionsList.Children.Add(SessionRow(s));
        _chrome = connected;
        if (_sessionsPopup?.Child is not FrameworkElement panel) return null;
        _sessionsPopup.IsOpen = true; panel.UpdateLayout();
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, () => { });
        var result = CaptureElement(panel, path);
        _sessionsPopup.IsOpen = false;
        return result;
    }
#endif
}
