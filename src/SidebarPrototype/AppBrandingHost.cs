namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    private readonly System.Windows.Threading.DispatcherTimer _brandingTimer = new() { Interval = TimeSpan.FromHours(6) };
    private string _brandName = AppBranding.TheUsuals;
    private System.Windows.Controls.TextBlock? _brandingHeader;
    private System.Windows.Window? _addAppDialog;

    private void InitializeBranding()
    {
        var selection = _options.Fixture
            ? AppBranding.Select(BrandingMode.TheUsuals, -1)
            : AppBranding.Select(_settings.BrandingMode, _settings.LastBrandIndex);
        _brandName = selection.Name;
        if (!_options.Fixture && _settings.BrandingMode == BrandingMode.Rotate)
        {
            _settings.LastBrandIndex = selection.LastBrandIndex;
            _ = SaveAsync();
        }
        UpdateBrandingDisplay();
        if (_options.Fixture) return;
        _brandingTimer.Tick += (_, _) => RotateBranding();
        _brandingTimer.Start();
    }

    private void RotateBranding()
    {
        var selection = AppBranding.Select(_settings.BrandingMode, _settings.LastBrandIndex);
        _brandName = selection.Name;
        if (_settings.BrandingMode == BrandingMode.Rotate)
        {
            _settings.LastBrandIndex = selection.LastBrandIndex;
            _ = SaveAsync();
        }
        UpdateBrandingDisplay();
    }

    private void UpdateBrandingDisplay()
    {
        Title = _brandName;
        if (_tray is not null) _tray.Text = _brandName;
        if (_brandingHeader is not null) _brandingHeader.Text = _brandName;
        if (_addAppDialog is not null) _addAppDialog.Title = $"Add App · {_brandName}";
    }
}
