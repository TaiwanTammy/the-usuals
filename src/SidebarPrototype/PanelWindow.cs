using System.Net;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SidebarPrototype.Shell;
using SidebarPrototype.Verification;
using System.Windows.Threading;
using System.Windows.Shell;
using System.Windows.Media;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using Control = System.Windows.Controls.Control;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace SidebarPrototype;

internal sealed class PanelWindow : Window
{
    private readonly HostWindow _host; private readonly DockPanel _root = new(); private readonly Grid _content = new(); private readonly Dictionary<WebView2, UIElement> _viewBoxes = []; private readonly List<AuthSurface> _popups = []; private UIElement? _failure; private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 12, Padding = new Thickness(8,3,8,3), Background = System.Windows.Media.Brushes.Black };
    private readonly DispatcherTimer _statusTimer = new() { Interval=TimeSpan.FromSeconds(3) };
    private WpfButton? _deviceButton, _modeButton, _titleButton, _topmostButton, _browserButton, _themeButton, _moreButton, _closeButton;
    private WpfButton? _backButton, _forwardButton, _reloadButton, _zoomOutButton, _zoomInButton;
    private TextBlock? _zoomPercent;
    private ContextMenu? _overflowMenu;
    private readonly Dictionary<string, WpfButton> _toolbarButtons = new();
    private Grid? _toolbar;
    private WebView2? _activeView; private string? _activeLauncher;
    private LauncherSetting? _activeSetting; private PanelMode? _displayedMode;
    private readonly WpfButton _outsideLink = new() { Visibility=Visibility.Collapsed,Padding=new Thickness(6),Margin=new Thickness(4) };
    private Action? _outsideAction;
    public bool HasActiveAuth => _popups.Any(p=>!p.IsClosed && p.LauncherId==_activeLauncher);
    public WebView2? ActiveAuthView(string id) => _popups.LastOrDefault(p=>!p.IsClosed && p.LauncherId==id)?.Browser;
    private bool _userCloseRequested;
    private bool _nativeHookInstalled;
    public int CloseAttemptCount { get; private set; }
    public int WindowPositionMessages { get; private set; }
    public int WindowMoveMessages { get; private set; }
    public int WindowSizeMessages { get; private set; }
    public event EventHandler? UserResizeCompleted;
    public PanelWindow(HostWindow host)
    {
        _host = host; Title = "Sidebar content · Edgebar"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Background = System.Windows.Media.Brushes.Black; ShowInTaskbar = false; Topmost = true;
        // Keep the panel visually flush with the dock while retaining a native right-edge resize target.
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0, 0, 6, 0), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        _status.Visibility=Visibility.Collapsed; _statusTimer.Tick+=(_,_)=> { _statusTimer.Stop(); _status.Visibility=Visibility.Collapsed; };
        Closing+=(_,e)=> { CloseAttemptCount++; if(!host.IsQuitting) { e.Cancel=true; var userRequested=_userCloseRequested; _userCloseRequested=false; if(userRequested) host.OnToolbar("close"); } };
        // The owner assigns its handle before the first reservation; install the diagnostic hook there.
        var bar = new Grid { Height=34, Background=System.Windows.Media.Brushes.Black, Margin=new Thickness(0), ClipToBounds=true };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star), MinWidth=0 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var title=AddText("Gmail A","account","Current app / account",double.NaN); title.HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch; title.HorizontalContentAlignment=System.Windows.HorizontalAlignment.Left; title.Margin=new Thickness(6,0,0,0); title.Padding=new Thickness(0,1,2,1); _titleButton=title; Grid.SetColumn(title,0); bar.Children.Add(title);
        var actions=new StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Center };
        WpfButton AddText(string label,string key,string tooltip,double width,bool selected=false) { var b=new WpfButton { Content=label,Width=width,Height=28,Padding=new Thickness(4,1,4,1),Margin=new Thickness(1,0,1,0),Foreground=Brushes.LightGray,Background=selected ? new SolidColorBrush(Color.FromRgb(48,48,48)) : Brushes.Transparent,BorderBrush=Brushes.Transparent,Style=HostWindow.FlatButtonStyle,FontSize=11,ToolTip=tooltip,Focusable=true }; b.Click+=(_,_)=> { host.OnToolbar(key); UpdateCompactToolbar(); }; _toolbarButtons[key]=b; return b; }
        WpfButton AddPath(string data,string key,string tooltip,double width,bool selected=false) { var b=AddText("",key,tooltip,width,selected); b.Content=new System.Windows.Shapes.Path { Data=Geometry.Parse(data),Stroke=Brushes.LightGray,StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Fill=Brushes.Transparent,Width=14,Height=14,Stretch=Stretch.Uniform }; System.Windows.Automation.AutomationProperties.SetName(b,tooltip); return b; }
        _backButton=AddPath("M15 4 L7 12 L15 20","back","Back",26);
        _forwardButton=AddPath("M9 4 L17 12 L9 20","forward","Forward",26);
        _reloadButton=AddPath("M20 11 A8 8 0 0 0 6 6 L4 9 M4 9 L4 4 M4 9 L9 9 M4 13 A8 8 0 0 0 18 18 L20 15 M20 15 L15 15 M20 15 L20 20","reload","Reload",26);
        var mode=AddPath("M3 4 H17 V20 H3 Z M7 8 H20 V20 H7 Z","mode","Switch between overlay and split layout",24); _modeButton=mode; actions.Children.Add(mode);
        _browserButton=AddPath("M14 3 H21 V10 M21 3 L11 13 M18 13 V20 H4 V6 H11","external","Open in browser",24); actions.Children.Add(_browserButton);
        _deviceButton=AddText("Desktop","identity","Switch desktop/mobile browser identity",52); actions.Children.Add(_deviceButton);
        var top=AddPath("M8 3 H16 L15 9 L18 12 H6 L9 9 Z M12 12 V21","top","Always on Top: on",24,true); _topmostButton=top; System.Windows.Automation.AutomationProperties.SetName(top,"Always on Top: on"); actions.Children.Add(top);
        _zoomOutButton=AddPath("M5 12 H19","zoom-","Zoom out",20); actions.Children.Add(_zoomOutButton);
        _zoomPercent=new TextBlock { Text="100%", Foreground=Brushes.LightGray, FontSize=10, VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=System.Windows.HorizontalAlignment.Center, Width=32, ToolTip="Current page zoom" }; actions.Children.Add(_zoomPercent);
        _zoomInButton=AddPath("M5 12 H19 M12 5 V19","zoom+","Zoom in",20); actions.Children.Add(_zoomInButton);
        _themeButton=AddPath("M12 3 A9 9 0 1 0 12 21 A4.5 9 0 0 1 12 3 Z","theme","Dark mode",24); actions.Children.Add(_themeButton);
        var more=AddPath("M5 12 A1 1 0 1 0 5.01 12 M12 12 A1 1 0 1 0 12.01 12 M19 12 A1 1 0 1 0 19.01 12","more","More actions",24); _moreButton=more; var menu=HostWindow.CreateFlatMenu(); _overflowMenu=menu;
        menu.PlacementTarget=more; menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;
        more.Click+=(_,_)=>menu.IsOpen=true; actions.Children.Add(more);
        _closeButton=AddPath("M6 6 L18 18 M18 6 L6 18","close","Hide panel and keep this app loaded. Right-click its dock icon to close the app.",24); actions.Children.Add(_closeButton);
        actions.Children.Insert(0,_backButton!); actions.Children.Insert(1,_forwardButton!); actions.Children.Insert(2,_reloadButton!);
        Grid.SetColumn(actions,1); bar.Children.Add(actions);
        RebuildOverflow();
        _toolbar=bar; bar.SizeChanged+=(_,_)=>UpdateCompactToolbar(); bar.Loaded+=(_,_)=>UpdateCompactToolbar();
        DockPanel.SetDock(bar, Dock.Top); _root.Children.Add(bar); DockPanel.SetDock(_outsideLink,Dock.Top); _root.Children.Add(_outsideLink); _outsideLink.Click+=(_,_)=> { _outsideLink.Visibility=Visibility.Collapsed; _outsideAction?.Invoke(); }; var contentSurface=new Grid(); contentSurface.Children.Add(_content); _status.HorizontalAlignment=System.Windows.HorizontalAlignment.Right; _status.VerticalAlignment=System.Windows.VerticalAlignment.Top; _status.Margin=new Thickness(8); contentSurface.Children.Add(_status); _root.Children.Add(contentSurface);
        bar.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { host.OnToolbar("close"); e.Handled = true; } };
        Content = _root;
    }
    private void UpdateCompactToolbar() {
        if(_toolbar is null) return;
        var available=_toolbar.ActualWidth - 4;
        const double required=302; // essential controls plus button gaps
        var showBack=available>=required+28;
        var showForward=available>=required+56;
        var showReload=available>=required+84;
        if(_backButton is not null) _backButton.Visibility=showBack ? Visibility.Visible : Visibility.Collapsed;
        if(_forwardButton is not null) _forwardButton.Visibility=showForward ? Visibility.Visible : Visibility.Collapsed;
        if(_reloadButton is not null) _reloadButton.Visibility=showReload ? Visibility.Visible : Visibility.Collapsed;
        if(_titleButton is not null) {
            var label=_host.ConfiguredLaunchers.FirstOrDefault(x=>x.Id==_activeLauncher)?.Label ?? "App";
            var compactTitle=_titleButton.ActualWidth<92;
            var shortLabel=_activeLauncher switch { "gmail-a" or "calendar-a"=>"A", "gmail-b" or "calendar-b"=>"B", _=>label.Trim().Length==0 ? "A" : label.Trim()[0].ToString().ToUpperInvariant() };
            _titleButton.Content=compactTitle ? shortLabel : label;
            _titleButton.ToolTip=label;
            System.Windows.Automation.AutomationProperties.SetName(_titleButton,label);
        }
        if(_deviceButton is not null) _deviceButton.Content=_activeSetting?.MobileMode==true ? "Mobile" : "Desktop";
        if(_zoomPercent is not null) _zoomPercent.Text=$"{Math.Round((_activeSetting?.Zoom ?? 1)*100)}%";
        if(_modeButton is not null) { _modeButton.Content=ModeIcon(_displayedMode ?? _activeSetting?.Mode ?? PanelMode.Overlay); _modeButton.ToolTip=(_displayedMode ?? _activeSetting?.Mode ?? PanelMode.Overlay)==PanelMode.Overlay ? "Drawer layout" : "Side-by-side layout"; }
        if(_themeButton is not null) { var dark=(_activeSetting?.Theme ?? SiteTheme.Dark)==SiteTheme.Dark; _themeButton.ToolTip=dark ? "Light mode" : "Dark mode"; _themeButton.Content=ToolbarIcon(dark ? "M12 3 A9 9 0 1 0 12 21 A4.5 9 0 0 1 12 3 Z" : "M12 3 V5 M12 19 V21 M3 12 H5 M19 12 H21 M5.6 5.6 L7 7 M17 17 L18.4 18.4 M18.4 5.6 L17 7 M7 17 L5.6 18.4 M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8"); }
        RebuildOverflow();
    }
    internal object CaptureToolbar(string path,double width) {
        var original=Width; Width=width;
        if(_toolbar is null) throw new InvalidOperationException("Toolbar is unavailable");
        UpdateLayout(); _toolbar.UpdateLayout(); UpdateCompactToolbar(); UpdateLayout(); _toolbar.UpdateLayout();
        var height=_toolbar.ActualHeight; var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(_toolbar.ActualWidth)),Math.Max(1,(int)Math.Ceiling(height)),96,96,System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(_toolbar);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(path)) encoder.Save(stream);
        var commands=_toolbarButtons.Where(p=>p.Value.Visibility==Visibility.Visible).Select(p=>p.Key).ToArray();
        var overflowCommands=_overflowMenu?.Items.OfType<MenuItem>().Select(i=>i.Tag?.ToString() ?? "").Where(s=>s.Length>0).ToArray() ?? [];
        var noDuplicates=!commands.Intersect(overflowCommands,StringComparer.Ordinal).Any();
        var noOverflow=_toolbarButtons.Values.Where(b=>b.Visibility==Visibility.Visible).All(b=> {
            var origin=b.TransformToAncestor(_toolbar).Transform(new Point(0,0));
            var end=b.TransformToAncestor(_toolbar).Transform(new Point(b.ActualWidth,b.ActualHeight));
            return origin.X>=-0.5 && origin.Y>=-0.5 && end.X<=_toolbar.ActualWidth+0.5 && end.Y<=_toolbar.ActualHeight+0.5;
        });
        var visibleControls=new[]{_modeButton,_browserButton,_deviceButton,_topmostButton,_zoomOutButton,_zoomInButton,_themeButton,_moreButton,_closeButton}.All(x=>x is { Visibility:Visibility.Visible } && x.ActualWidth>0 && x.ActualHeight>0);
        var result=new { toolbarWidth=_toolbar.ActualWidth,toolbarHeight=height,visibleControls,commands,overflowcommands=overflowCommands,noDuplicates,noOverflow,compact=_toolbar.ActualWidth<=360 };
        Width=original; UpdateLayout(); UpdateCompactToolbar(); return result;
    }
    internal object CaptureOverflow(string path,double width) { var original=Width; Width=width; UpdateLayout(); UpdateCompactToolbar(); UpdateLayout(); _toolbar?.UpdateLayout(); _overflowMenu!.IsOpen=true; _overflowMenu.UpdateLayout(); Visual? popupContent=VisualTreeHelper.GetChildrenCount(_overflowMenu)>0 ? VisualTreeHelper.GetChild(_overflowMenu,0) as Visual : null; if(popupContent is System.Windows.Controls.Primitives.Popup popup) popupContent=popup.Child as Visual; popupContent ??= _overflowMenu; var bounds=VisualTreeHelper.GetDescendantBounds(popupContent); var size=bounds.IsEmpty ? ((FrameworkElement)popupContent).RenderSize : bounds.Size; var bmp=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(size.Width)),Math.Max(1,(int)Math.Ceiling(size.Height)),96,96,PixelFormats.Pbgra32); bmp.Render(popupContent); var enc=new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using(var s=File.Create(path)) enc.Save(s); var result=new { width=size.Width,height=size.Height,commands=_overflowMenu.Items.OfType<MenuItem>().Select(i=>i.Tag?.ToString() ?? "").ToArray() }; _overflowMenu.IsOpen=false; Width=original; UpdateLayout(); UpdateCompactToolbar(); return result; }
    internal object CapturePanel(string path,double width) { var original=Width; Width=width; UpdateLayout(); var size=_root.RenderSize; var bmp=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(size.Width)),Math.Max(1,(int)Math.Ceiling(size.Height)),96,96,PixelFormats.Pbgra32); bmp.Render(_root); var enc=new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using(var s=File.Create(path)) enc.Save(s); Width=original; UpdateLayout(); return new { width=size.Width,height=size.Height,windowStyle=WindowStyle.ToString(),resizeMode=ResizeMode.ToString(),resizeBorder=WindowChrome.GetWindowChrome(this)?.ResizeBorderThickness.ToString() }; }
    private static UIElement ToolbarIcon(string data) => new System.Windows.Shapes.Path { Data=Geometry.Parse(data),Stroke=Brushes.LightGray,StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Fill=Brushes.Transparent,Width=14,Height=14,Stretch=Stretch.Uniform };
    private static UIElement ModeIcon(PanelMode mode) => ToolbarIcon(mode==PanelMode.Overlay ? "M3 4 H18 V20 H3 Z M9 8 H21 V21 H9 Z" : "M3 4 H21 V20 H3 Z M12 4 V20");
    private void RebuildOverflow() {
        if(_overflowMenu is null) return;
        _overflowMenu.Items.Clear();
        var actions=new[]{(_backButton,"Back","back"),(_forwardButton,"Forward","forward"),(_reloadButton,"Reload","reload")}.Where(x=>x.Item1?.Visibility!=Visibility.Visible);
        foreach(var (_,label,key) in actions) { var item=new MenuItem { Header=label,Tag=key,Style=HostWindow.FlatMenuItemStyle }; item.Click+=(_,_)=> { _host.OnToolbar(key); UpdateCompactToolbar(); }; _overflowMenu.Items.Add(item); }
        if(_overflowMenu.Items.Count>0) _overflowMenu.Items.Add(new Separator());
        var reset=new MenuItem { Header="Reset zoom to 100%",Tag="zoom0",Style=HostWindow.FlatMenuItemStyle }; reset.Click+=(_,_)=> { _host.OnToolbar("zoom0"); UpdateCompactToolbar(); }; _overflowMenu.Items.Add(reset);
    }
    public void AddView(WebView2 v) { _viewBoxes[v] = v; _content.Children.Add(v); v.Visibility = Visibility.Collapsed; }
    internal void EnsureNativeWindowConfigured() { var hwnd=new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle(); _host.ApplyUtilityToolWindowStyle(this,"panel"); var source=System.Windows.Interop.HwndSource.FromHwnd(hwnd); if(source is not null && !_nativeHookInstalled) { source.AddHook(WindowProc); _nativeHookInstalled=true; } }
    public void SetActive(Launcher launcher, LauncherSetting setting, WebView2 view) { _activeLauncher=launcher.Id; _activeView=view; _outsideLink.Visibility=Visibility.Collapsed; _error.Text=""; ClearFailure(); RefreshVisibility(); RefreshControls(setting); }
    public void ClearActiveApp(string id) { if(_activeLauncher!=id) return; _activeLauncher=null; _activeView=null; _activeSetting=null; _displayedMode=null; _outsideLink.Visibility=Visibility.Collapsed; ClearFailure(); foreach(var box in _viewBoxes.Values) box.Visibility=Visibility.Collapsed; }
    private void RefreshVisibility() {
        var auth=_popups.LastOrDefault(p=>!p.IsClosed && p.LauncherId==_activeLauncher);
        foreach(var (view,box) in _viewBoxes) box.Visibility=auth is null && ReferenceEquals(view,_activeView) ? Visibility.Visible : Visibility.Collapsed;
        foreach(var popup in _popups) popup.Visibility=ReferenceEquals(auth,popup) ? Visibility.Visible : Visibility.Collapsed;
        if(_activeSetting is not null) RefreshControls(_activeSetting,_displayedMode);
    }
    public void RefreshControls(LauncherSetting setting, PanelMode? displayedMode = null) {
        _activeSetting=setting; _displayedMode=displayedMode;
        if(_deviceButton is not null) { _deviceButton.Content=setting.MobileMode ? "Mobile" : "Desktop"; _deviceButton.IsEnabled=!HasActiveAuth; }
        if(_modeButton is not null) _modeButton.Content=(displayedMode ?? setting.Mode).ToString();
        if(_topmostButton is not null) { var top=Window.GetWindow(this)?.Topmost==true; _topmostButton.Background=top ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(55,55,55)) : System.Windows.Media.Brushes.Transparent; _topmostButton.ToolTip=top ? "Always on Top: on" : "Always on Top: off"; System.Windows.Automation.AutomationProperties.SetName(_topmostButton,_topmostButton.ToolTip.ToString()); }
        UpdateCompactToolbar();
    }
    public void OfferExternalLink(Uri uri,Action action) { _outsideAction=action; _outsideLink.Content=$"Open {uri.IdnHost} in browser"; _outsideLink.Visibility=Visibility.Visible; SetStatus("An outside link was requested. It has not opened automatically."); }
    public void PromotePopup(AuthSurface surface,WebView2 view) { _popups.Remove(surface); surface.MarkPromoted(); _viewBoxes[view]=surface; if(_activeLauncher==surface.LauncherId) _activeView=view; RefreshVisibility(); }
    private nint WindowProc(nint hwnd,int message,nint wParam,nint lParam,ref bool handled) { if(_host.TransitionTest) { if(message==0x0046) _host.RecordNativeBounds("panel",hwnd,message,lParam); else if(message==0x0047) { WindowPositionMessages++; _host.RecordNativeBounds("panel",hwnd,message); } else if(message==0x0003) { WindowMoveMessages++; _host.RecordNativeBounds("panel",hwnd,message); } else if(message==0x0005) { WindowSizeMessages++; _host.RecordNativeBounds("panel",hwnd,message); } } if(message==0x0112 && (wParam.ToInt64() & 0xfff0)==0xf060) _userCloseRequested=true; if(message==0x0232) UserResizeCompleted?.Invoke(this,EventArgs.Empty); return 0; }
    public void ApplyLayout(DockLayout l, double width) { Left = l.PanelBounds.Left; Top = l.PanelBounds.Top; Width = Math.Min(width, l.PanelBounds.Width); Height = l.PanelBounds.Height; }
    public void ShowFailure(Launcher l, Action retry) { ClearFailure(); var stack = new StackPanel { Margin = new Thickness(20), Background = System.Windows.Media.Brushes.Black }; stack.Children.Add(new TextBlock { Text = $"Could not open {l.Label}", Foreground = System.Windows.Media.Brushes.White, FontSize = 18 }); stack.Children.Add(_error); var b = new WpfButton { Content = "Retry", Margin = new Thickness(0, 12, 0, 0) }; b.Click += (_, _) => retry(); stack.Children.Add(b); _failure = stack; _content.Children.Add(stack); }
    public void SetError(string text) => _error.Text = text;
    public void SetStatus(string text) { _status.Text=text; _statusTimer.Stop(); _status.Visibility=string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible; if(_status.Visibility==Visibility.Visible) _statusTimer.Start(); }
    internal bool HasFailure => _failure is not null;
    public void TrackPopup(AuthSurface p) { _popups.Add(p); _content.Children.Add(p); p.Closed+=(_,_)=> { _popups.Remove(p); _content.Children.Remove(p); RefreshVisibility(); }; RefreshVisibility(); }
    public void RemoveView(WebView2 v) { if(_viewBoxes.Remove(v,out var box)) _content.Children.Remove(box); }
    internal bool OwnsView(WebView2 view) => _viewBoxes.ContainsKey(view);
    internal bool OwnsPopup(AuthSurface surface) => _popups.Contains(surface);
    public void ClearFailure() { if (_failure is not null) _content.Children.Remove(_failure); _failure = null; }
    public bool CloseTopPopup() { var popup = _popups.LastOrDefault(p=>p.LauncherId==_activeLauncher); if (popup is null) return false; popup.Close(); return true; }
    public void ClosePopups(string launcherId) { foreach(var p in _popups.Where(p=>p.LauncherId==launcherId).ToArray()) try { p.Close(); } catch { } }
    public void ClosePopups() { foreach (var p in _popups.ToArray()) try { p.Close(); } catch { } _popups.Clear(); }
}
