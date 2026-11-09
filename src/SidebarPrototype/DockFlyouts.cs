using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SidebarPrototype;

// The + and gear buttons open panels that hover out beside the dock, styled like the Chrome sessions list.
// Removing an app from the dock also lives here.
internal sealed partial class HostWindow
{
    private Popup? _settingsFlyout, _addFlyout;
    private string? _pendingIconFile;

    private Popup CreateFlyout(UIElement target, double width) => new()
    {
        PlacementTarget = target, Placement = PlacementMode.Right, HorizontalOffset = 6, StaysOpen = false, AllowsTransparency = true,
        Child = new Border { Width = width, Background = BrushFor(30), BorderBrush = BrushFor(72), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6) },
    };

    private static void SetFlyoutContent(Popup popup, UIElement content) => ((Border)popup.Child).Child = content;

    private static TextBlock FlyoutHeading(string text) =>
        new() { Text = text, Foreground = SessionMuted, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 6, 10, 6), TextTrimming = TextTrimming.CharacterEllipsis };

    private static Border FlyoutDivider() => new() { Height = 1, Background = BrushFor(56), Margin = new Thickness(6, 5, 6, 5) };

    private static Border FlyoutRow(string label, string? value, Action click, UIElement? icon = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon is not null) { if (icon is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 10, 0); grid.Children.Add(icon); }
        var name = new TextBlock { Text = label, Foreground = SessionText, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        if (value is not null)
        {
            var v = new TextBlock { Text = value, Foreground = SessionMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(v, 2); grid.Children.Add(v);
        }
        var row = new Border { Child = grid, Height = 30, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        AutomationProperties.SetName(row, label);
        row.MouseEnter += (_, _) => row.Background = BrushFor(48);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    // ---- Settings ----

    private WpfButton CreateSettingsButton()
    {
        var settings = new WpfButton { Content = new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = Brushes.LightGray }, Width = 36, Height = 36, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Style = FlatButtonStyle, ToolTip = "Settings", Tag = "settings" };
        AutomationProperties.SetName(settings, "Settings");
        _settingsFlyout = CreateFlyout(settings, 250);
        settings.Click += (_, _) => { if (!_settingsFlyout.IsOpen) RenderSettings(); _settingsFlyout.IsOpen = !_settingsFlyout.IsOpen; };
        return settings;
    }

    private void RenderSettings()
    {
        var list = new StackPanel();
        _brandingHeader = FlyoutHeading(_brandName);
        list.Children.Add(_brandingHeader);
        list.Children.Add(FlyoutRow("Keyboard shortcut", ShortcutText(), () => { _settingsFlyout!.IsOpen = false; ConfigureShortcut(); }));
        list.Children.Add(FlyoutDivider());
        list.Children.Add(FlyoutRow("Hide sidebar", null, () => { _settingsFlyout!.IsOpen = false; HideUtility(); }));
        list.Children.Add(FlyoutRow("Quit", null, () => { _settingsFlyout!.IsOpen = false; Quit(); }));
        SetFlyoutContent(_settingsFlyout!, list);
    }

    private string ShortcutText()
    {
        var m = _settings.ShortcutModifiers; var parts = new List<string>();
        if ((m & 0x2) != 0) parts.Add("Ctrl");
        if ((m & 0x1) != 0) parts.Add("Alt");
        if ((m & 0x4) != 0) parts.Add("Shift");
        if ((m & 0x8) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)_settings.ShortcutVirtualKey).ToString();
        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key[1..];
        parts.Add(key);
        return string.Join(" + ", parts);
    }

    // ---- Add app ----

    private WpfButton CreateAddButton()
    {
        var add = CreateDockButton("+", "Add App", "Add App", 36, 36);
        add.FontSize = 18;
        _addFlyout = CreateFlyout(add, 300);
        add.Click += (_, _) => { if (!_addFlyout.IsOpen) RenderAddApp(); _addFlyout.IsOpen = !_addFlyout.IsOpen; };
        return add;
    }

    private static (Grid field, TextBox box) FlyoutTextBox(string hint)
    {
        var box = new TextBox { Height = 30, Padding = new Thickness(7, 5, 7, 5), Background = BrushFor(40), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = BrushFor(70), BorderThickness = new Thickness(1), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, hint);
        var placeholder = new TextBlock { Text = hint, Foreground = BrushFor(120), FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid { Margin = new Thickness(4, 0, 4, 6) };
        field.Children.Add(box); field.Children.Add(placeholder);
        return (field, box);
    }

    private void RenderAddApp()
    {
        _pendingIconFile = null;
        var list = new StackPanel();
        list.Children.Add(FlyoutHeading("Add app"));
        var (urlField, url) = FlyoutTextBox("Website address");
        var (nameField, name) = FlyoutTextBox("Name (optional)");
        list.Children.Add(urlField); list.Children.Add(nameField);

        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 150)), FontSize = 12, Margin = new Thickness(6, 0, 6, 6), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        list.Children.Add(error);

        var actions = new Grid { Margin = new Thickness(4, 0, 4, 2) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconLabel = new TextBlock { Text = "Choose icon", TextTrimming = TextTrimming.CharacterEllipsis };
        var iconButton = new WpfButton { Content = iconLabel, Height = 28, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, MaxWidth = 170, Style = FlatButtonStyle, Foreground = SessionMuted, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, FontSize = 12, ToolTip = "Choose an icon file" };
        AutomationProperties.SetName(iconButton, "Choose an icon file");
        var addButton = new WpfButton { Content = "Add", Width = 64, Height = 28, Style = FlatButtonStyle, Foreground = Brushes.White, Background = BrushFor(58), BorderBrush = Brushes.Transparent, FontSize = 12 };
        AutomationProperties.SetName(addButton, "Add");
        Grid.SetColumn(addButton, 1);
        actions.Children.Add(iconButton); actions.Children.Add(addButton);
        list.Children.Add(actions);

        iconButton.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose an app icon", Filter = "Images|*.png;*.jpg;*.jpeg;*.ico;*.bmp" };
            _addFlyout!.StaysOpen = true; // the file picker would otherwise close the panel
            try { if (picker.ShowDialog(this) == true) { _pendingIconFile = picker.FileName; iconLabel.Text = Path.GetFileName(picker.FileName); iconButton.Foreground = SessionText; } }
            finally { _addFlyout.StaysOpen = false; _addFlyout.IsOpen = true; }
        };
        void Submit()
        {
            try
            {
                var normalized = LauncherCatalog.NormalizeUrl(url.Text);
                var label = string.IsNullOrWhiteSpace(name.Text) ? new Uri(normalized).Host.Replace("www.", "") : name.Text;
                if (!TryAddLauncher(label, normalized, _pendingIconFile, out var message)) throw new FormatException(message);
                _addFlyout!.IsOpen = false;
                _ = ActivateFromDockAsync(ConfiguredLaunchers.Last());
            }
            catch (Exception e) { error.Text = e.Message; error.Visibility = Visibility.Visible; }
        }
        addButton.Click += (_, _) => Submit();
        System.Windows.Input.KeyEventHandler keys = (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } else if (e.Key == Key.Escape) { _addFlyout!.IsOpen = false; e.Handled = true; } };
        url.KeyDown += keys; name.KeyDown += keys;

        var removed = Launchers.All.Where(x => _settings.RemovedLaunchers.Contains(x.Id)).ToArray();
        if (removed.Length > 0)
        {
            list.Children.Add(FlyoutDivider());
            list.Children.Add(FlyoutHeading("Put back"));
            foreach (var app in removed)
            {
                var asset = GetLauncherAsset(app);
                var icon = new System.Windows.Controls.Image { Source = LoadIcon(asset + "-color-128.png"), Width = 16, Height = 16 };
                list.Children.Add(FlyoutRow(app.Label, null, () => { _addFlyout!.IsOpen = false; RestoreLauncher(app.Id, open: true); }, icon));
            }
        }
        SetFlyoutContent(_addFlyout!, list);
        _addFlyout!.Opened -= FocusAddressOnOpen; _addFlyout.Opened += FocusAddressOnOpen;
        void FocusAddressOnOpen(object? s, EventArgs e) { if (!_options.UiTest) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { Activate(); url.Focus(); Keyboard.Focus(url); }); }
    }

    // ---- Remove / put back ----

    // Takes an app off the dock. Browser data is never deleted; built-in apps come back from the + panel still
    // signed in, while a re-added custom site gets a new profile.
    private void RemoveLauncher(Launcher app)
    {
        UnloadLauncher(app);
        if (app.Id.StartsWith("custom-", StringComparison.Ordinal))
        {
            if (_settings.CustomLaunchers.FirstOrDefault(x => x.Id == app.Id) is { } definition)
            {
                _settings.CustomLaunchers.Remove(definition);
                if (definition.IconPath is { } icon) try { File.Delete(Path.Combine(_root, "customsitefiles", Path.GetFileName(icon))); } catch { }
            }
            _settings.Launchers.Remove(app.Id);
        }
        else if (!_settings.RemovedLaunchers.Contains(app.Id)) _settings.RemovedLaunchers.Add(app.Id);
        _settings.LauncherOrder.Remove(app.Id);
        if (_lastLauncherId == app.Id) _lastLauncherId = null;
        RefreshConfiguredLaunchers(); RebuildLauncherButtons(); _ = SaveAsync();
    }

    private void RestoreLauncher(string id, bool open)
    {
        if (!_settings.RemovedLaunchers.Remove(id)) return;
        _settings.LauncherOrder = ConfiguredLaunchers.Select(x => x.Id).Append(id).ToList();
        RefreshConfiguredLaunchers(); RebuildLauncherButtons(); _ = SaveAsync();
        if (open && ConfiguredLaunchers.FirstOrDefault(x => x.Id == id) is { } launcher) _ = ActivateFromDockAsync(launcher);
    }

    internal object? CaptureSettingsMenu(string path) => CaptureFlyout(_settingsFlyout, RenderSettings, path);
    internal object? CaptureAddApp(string path) => CaptureFlyout(_addFlyout, RenderAddApp, path);
    private object? CaptureFlyout(Popup? popup, Action render, string path)
    {
        if (popup?.Child is not FrameworkElement panel) return null;
        render(); popup.IsOpen = true; panel.UpdateLayout();
        Dispatcher.Invoke(DispatcherPriority.Render, () => { });
        var result = CaptureElement(panel, path);
        popup.IsOpen = false;
        return result;
    }
    internal void RemoveLauncherForTest(string id) { if (ConfiguredLaunchers.FirstOrDefault(x => x.Id == id) is { } app) RemoveLauncher(app); }
    internal void RestoreLauncherForTest(string id) => RestoreLauncher(id, open: false);
}
