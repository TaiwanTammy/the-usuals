using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using Control = System.Windows.Controls.Control;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    private readonly Grid _dock = new();
    private readonly StackPanel _appButtons = new() { Orientation = System.Windows.Controls.Orientation.Vertical };
    private readonly StackPanel _bottomButtons = new() { Orientation = System.Windows.Controls.Orientation.Vertical };
    private readonly List<WpfButton> _launcherButtons = [];
    private string? _dragLauncherId;
    private WpfPoint _dragStart;
    private bool _suppressDockClick;
    private readonly DispatcherTimer _dragClickReset = new() { Interval=TimeSpan.FromMilliseconds(350) };
    private string? _pendingIconFile;

    private IReadOnlyList<Launcher> _configuredLaunchers = Launchers.All;
    internal IReadOnlyList<Launcher> ConfiguredLaunchers => _configuredLaunchers;

    internal bool TryAddLauncher(string name, string url, string? iconSource, out string? error)
    {
        error=null;
        try {
            if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>48) throw new FormatException("Enter a name of 1 to 48 characters.");
            var normalized=LauncherCatalog.NormalizeUrl(url); var id="custom-"+Guid.NewGuid().ToString("N"); var profile="Custom-"+id[7..];
            string? storedIcon=null;
            if(iconSource is not null) {
                if(!File.Exists(iconSource)) throw new FileNotFoundException("The selected icon file could not be found.");
                var ext=Path.GetExtension(iconSource).ToLowerInvariant(); if(!new[]{".png",".jpg",".jpeg",".bmp",".ico"}.Contains(ext)) throw new FormatException("Choose a PNG, JPG, BMP, or ICO image.");
                var dir=Path.Combine(_root,"customsitefiles"); Directory.CreateDirectory(dir); storedIcon=id+ext; File.Copy(iconSource,Path.Combine(dir,storedIcon),false);
            }
            var definition=new CustomLauncherDefinition { Id=id,Label=name.Trim(),Url=normalized,Profile=profile,IconPath=storedIcon };
            _settings.CustomLaunchers.Add(definition); _settings.Launchers[id]=new LauncherSetting { Mode=PanelMode.Overlay,Theme=SiteTheme.Dark };
            _settings.LauncherOrder=ConfiguredLaunchers.Select(x=>x.Id).Append(id).ToList(); RefreshConfiguredLaunchers(); RebuildLauncherButtons(); _=SaveAsync();
            if(iconSource is null) _=LoadFaviconAfterAddAsync(definition);
            return true;
        } catch(Exception ex) { error=ex.Message; return false; }
    }

    private async Task LoadFaviconAfterAddAsync(CustomLauncherDefinition definition)
    {
        try {
            var dir=Path.Combine(_root,"customsitefiles"); var icon=await TrySaveWebsiteFaviconAsync(definition.Id,definition.Url,dir).ConfigureAwait(false); if(icon is null)return;
            await Dispatcher.InvokeAsync(()=>{ var current=_settings.CustomLaunchers.FirstOrDefault(x=>x.Id==definition.Id); if(current is not null) { current.IconPath=icon; RebuildLauncherButtons(); } });
            await SaveAsync().ConfigureAwait(false);
        } catch { }
    }

    private void RefreshConfiguredLaunchers()
    {
        _configuredLaunchers = _fixture && !_uiTest ? Launchers.All : LauncherCatalog.For(_settings);
    }

    internal void ReorderLaunchers(IReadOnlyList<string> ids)
    {
        var valid=ConfiguredLaunchers.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        var order=ids.Where(valid.Contains).Distinct(StringComparer.Ordinal).ToList();
        order.AddRange(ConfiguredLaunchers.Select(x=>x.Id).Where(x=>!order.Contains(x,StringComparer.Ordinal)));
        _settings.LauncherOrder=order; RefreshConfiguredLaunchers(); RebuildLauncherButtons(); _=SaveAsync();
    }

    private bool HandleDrop(string sourceId,string targetId)
    {
        var ids=ConfiguredLaunchers.Select(x=>x.Id).ToList(); var from=ids.IndexOf(sourceId); var to=ids.IndexOf(targetId); if(from<0||to<0||from==to)return false;
        ids.RemoveAt(from); ids.Insert(to,sourceId); ReorderLaunchers(ids); return true;
    }

    internal bool ExerciseDockDropForTest(string sourceId,string targetId) => HandleDrop(sourceId,targetId);

    private void BuildButtons()
    {
        _dragClickReset.Tick += ResetSuppressedClick;
        _dock.Background = Brushes.Black;
        _dock.Margin = new Thickness(4, 12, 4, 8);
        _dock.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _dock.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll=new ScrollViewer { Content=_appButtons, VerticalScrollBarVisibility=ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, CanContentScroll=true };
        Grid.SetRow(scroll, 0); Grid.SetRow(_bottomButtons, 1);
        _dock.Children.Add(scroll); _dock.Children.Add(_bottomButtons);
        RebuildLauncherButtons();

        var add = CreateDockButton("+", "Add App", "Add App", 36, 36);
        AutomationProperties.SetName(add, "Add App");
        add.Click += (_, _) => ShowAddAppDialog();
        _bottomButtons.Children.Add(add);

        var settings = new WpfButton { Content = new TextBlock { Text="\uE713", FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize=16, Foreground=Brushes.LightGray }, Width=36, Height=36, Margin=new Thickness(0,4,0,0), Padding=new Thickness(8), Background=Brushes.Transparent, BorderBrush=Brushes.Transparent, Style=FlatButtonStyle, ToolTip="Settings", Tag="settings" };
        AutomationProperties.SetName(settings, "Settings");
        settings.ContextMenu = CreateFlatMenu();
        _brandingHeader = new TextBlock { Text=_brandName, Foreground=BrushFor(170), FontSize=12, FontWeight=FontWeights.SemiBold, TextTrimming=TextTrimming.CharacterEllipsis };
        settings.ContextMenu.Items.Add(new Border { Child=_brandingHeader, Padding=new Thickness(12,8,16,8), IsHitTestVisible=false, Focusable=false });
        var shortcut = NewMenuItem("Keyboard shortcut…"); shortcut.Click += (_, _) => ConfigureShortcut(); settings.ContextMenu.Items.Add(shortcut);
        settings.ContextMenu.Items.Add(new Border { Height=1, Background=BrushFor(70), Margin=new Thickness(8,3,8,3) });
        var hide = NewMenuItem("Hide sidebar"); hide.Click += (_, _) => HideUtility(); settings.ContextMenu.Items.Add(hide);
        var quit = NewMenuItem("Quit sidebar"); quit.Click += (_, _) => Quit(); settings.ContextMenu.Items.Add(quit);
        settings.Click += (_, _) => settings.ContextMenu.IsOpen = true;
        _bottomButtons.Children.Add(settings);
    }

    private void RebuildLauncherButtons()
    {
        _appButtons.Children.Clear(); _launcherButtons.Clear();
        Launcher? previous = null;
        foreach (var launcher in ConfiguredLaunchers)
        {
            if (previous is not null && !StringComparer.Ordinal.Equals(UiGroup(previous), UiGroup(launcher)))
            {
                var divider = new Border { Height=1, Margin=new Thickness(8,7,8,7), Background=BrushFor(72), Tag="account-divider", ToolTip=$"{previous.Label} / {launcher.Label}" };
                AutomationProperties.SetName(divider, "Account divider");
                _appButtons.Children.Add(divider);
            }
            var button = CreateLauncherButton(launcher);
            _launcherButtons.Add(button); _appButtons.Children.Add(button);
            previous = launcher;
        }
        MarkActiveDockButton(_state.IsVisible ? _state.ActiveId : null);
    }

    private WpfButton CreateLauncherButton(Launcher launcher)
    {
        var asset = GetLauncherAsset(launcher);
        var icon = new Grid { Width=32, Height=32, HorizontalAlignment=System.Windows.HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Tag="launcher-icon" };
        var grey = LoadIcon(asset + "-gray-128.png");
        var color = asset.StartsWith("custom:", StringComparison.Ordinal) ? LoadCustomIcon(asset[7..]) : LoadIcon(asset + "-color-128.png");
        var normal = grey ?? (color is BitmapSource colorBitmap ? new FormatConvertedBitmap(colorBitmap,PixelFormats.Gray8,null,0) : null);
        if (normal is null) { icon.Children.Add(new TextBlock { Text=launcher.Label[..1].ToUpperInvariant(), Foreground=Brushes.LightGray, FontSize=17, FontWeight=FontWeights.SemiBold, HorizontalAlignment=System.Windows.HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Tag="icon-fallback" }); }
        var imageGray = new System.Windows.Controls.Image { Source=grey, Width=27, Height=27, Stretch=Stretch.Uniform, HorizontalAlignment=System.Windows.HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Tag="icon-grayscale" };
        var imageColor = new System.Windows.Controls.Image { Source=color ?? grey, Width=27, Height=27, Stretch=Stretch.Uniform, HorizontalAlignment=System.Windows.HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, Opacity=0, Tag="icon-color" };
        if (normal is not null) { imageGray.Source=normal; icon.Children.Add(imageGray); icon.Children.Add(imageColor); }
        if (launcher.Profile.StartsWith("Account", StringComparison.Ordinal))
        {
            var badge = new Border { Background=BrushFor(52), CornerRadius=new CornerRadius(5), Padding=new Thickness(3,0,3,0), HorizontalAlignment=System.Windows.HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Bottom, Child=new TextBlock { Text=launcher.Profile[^1..], FontSize=8, FontWeight=FontWeights.Bold, Foreground=Brushes.White } };
            icon.Children.Add(badge);
        }
        var button = new WpfButton { Content=icon, Tag=launcher.Id, Height=46, Margin=new Thickness(0,1,0,1), Foreground=Brushes.White, Background=Brushes.Transparent, BorderBrush=Brushes.Transparent, Style=FlatButtonStyle, ToolTip=launcher.Label, Focusable=true };
        button.SetValue(IsDockActiveProperty,_state.ActiveId==launcher.Id);
        AutomationProperties.SetName(button, launcher.Label);
        var appMenu = CreateFlatMenu(); var unload = NewMenuItem("Close"); unload.ToolTip="Unload this app from memory; account sign-in stays saved."; unload.Click += (_, _) => UnloadLauncher(launcher); appMenu.Items.Add(unload); button.ContextMenu=appMenu;
        button.MouseEnter += (_, _) => imageColor.Opacity = 1;
        button.MouseLeave += (_, _) => imageColor.Opacity = 0;
        button.Click += async (_, _) => { if(_suppressDockClick)return; await ActivateFromDockAsync(launcher); };
        button.AllowDrop=true;
        button.PreviewMouseLeftButtonDown += (_, e) => { _dragLauncherId=launcher.Id; _dragStart=e.GetPosition(button); };
        button.PreviewMouseLeftButtonUp += (_, _) => _dragLauncherId = null;
        button.PreviewMouseMove += (_, e) =>
        {
            if (_dragLauncherId!=launcher.Id || e.LeftButton != MouseButtonState.Pressed) return;
            var point=e.GetPosition(button);
            if(Math.Abs(point.X-_dragStart.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y-_dragStart.Y)<SystemParameters.MinimumVerticalDragDistance) return;
            e.Handled=true; System.Windows.DragDrop.DoDragDrop(button,new System.Windows.DataObject("SidebarPrototype.LauncherId",launcher.Id),System.Windows.DragDropEffects.Move); _dragLauncherId=null; _suppressDockClick=true; _dragClickReset.Stop(); _dragClickReset.Start();
        };
        button.DragOver+=(_,e)=>{ e.Effects=e.Data.GetDataPresent("SidebarPrototype.LauncherId")?System.Windows.DragDropEffects.Move:System.Windows.DragDropEffects.None; e.Handled=true; };
        button.Drop+=(_,e)=>{ if(e.Data.GetData("SidebarPrototype.LauncherId") is string source) HandleDrop(source,launcher.Id); e.Handled=true; };
        button.LostMouseCapture+=(_,_)=>_dragLauncherId=null;
        return button;
    }

    private static WpfButton CreateDockButton(string text, string name, string tooltip, double width, double height)
    {
        var b = new WpfButton { Content=text, Width=width, Height=height, Margin=new Thickness(0,1,0,1), Padding=new Thickness(4), Background=Brushes.Transparent, BorderBrush=Brushes.Transparent, Foreground=Brushes.LightGray, Style=FlatButtonStyle, ToolTip=tooltip };
        AutomationProperties.SetName(b, name); return b;
    }

    private static readonly DependencyProperty IsMenuPressedProperty=DependencyProperty.RegisterAttached("IsMenuPressed",typeof(bool),typeof(HostWindow),new FrameworkPropertyMetadata(false));
    private static MenuItem NewMenuItem(string label)
    {
        var item=new MenuItem { Header=label, Style=FlatMenuItemStyle, Focusable=true };
        item.PreviewMouseLeftButtonDown+=(_,_)=>item.SetValue(IsMenuPressedProperty,true);
        item.PreviewMouseLeftButtonUp+=(_,_)=>item.SetValue(IsMenuPressedProperty,false);
        item.LostMouseCapture+=(_,_)=>item.SetValue(IsMenuPressedProperty,false);
        return item;
    }
    private static ImageSource? LoadIcon(string name)
    {
        try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze(); return bitmap; } catch { return null; }
    }
    private ImageSource? LoadCustomIcon(string file)
    {
        try { var path=Path.Combine(_root,"customsitefiles",Path.GetFileName(file)); if(!File.Exists(path)) return null; var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource=new Uri(path); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze(); return bitmap; } catch { return null; }
    }
    private string GetLauncherAsset(Launcher launcher)
    {
        if (launcher.Id.StartsWith("custom-", StringComparison.Ordinal)) return _settings.CustomLaunchers.FirstOrDefault(x=>x.Id==launcher.Id)?.IconPath is { } icon ? "custom:"+icon : "custom:missing";
        return launcher.Id.StartsWith("gmail",StringComparison.Ordinal) ? "gmail" : launcher.Id.StartsWith("calendar",StringComparison.Ordinal) ? "calendar" : "soundcloud";
    }
    private static string UiGroup(Launcher launcher) => launcher.Profile switch { "AccountA"=>"account-a", "AccountB"=>"account-b", "SoundCloud"=>"soundcloud", _=>"custom-apps" };
    private static readonly DependencyProperty IsDockActiveProperty=DependencyProperty.RegisterAttached("IsDockActive",typeof(bool),typeof(HostWindow),new FrameworkPropertyMetadata(false));
    private void MarkActiveDockButton(string? id) { foreach(var button in _launcherButtons) button.SetValue(IsDockActiveProperty,Equals(button.Tag,id)); }
    private void ResetSuppressedClick(object? sender,EventArgs e) { _dragClickReset.Stop(); _suppressDockClick=false; }

    internal static Style FlatButtonStyle { get; } = CreateFlatButtonStyle();
    internal static Style FlatMenuItemStyle { get; } = CreateFlatMenuItemStyle();
    internal static ContextMenu CreateFlatMenu() => new() { Background=BrushFor(30), BorderBrush=BrushFor(72), BorderThickness=new Thickness(1), HasDropShadow=false, Template=CreateContextMenuTemplate() };
    private static SolidColorBrush BrushFor(byte v) => new(Color.FromRgb(v,v,v));
    private static System.Windows.Shapes.Path VectorIcon(string data, bool filled) => new() { Data=Geometry.Parse(data), Stroke=Brushes.LightGray, StrokeThickness=1.5, Fill=filled?Brushes.LightGray:Brushes.Transparent, Width=16, Height=16, Stretch=Stretch.Uniform };

    private static Style CreateFlatButtonStyle()
    {
        var style=new Style(typeof(WpfButton)); var template=new ControlTemplate(typeof(WpfButton));
        var border=new FrameworkElementFactory(typeof(Border),"ButtonBorder"); border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty)); border.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));
        var content=new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(ContentPresenter.HorizontalAlignmentProperty,System.Windows.HorizontalAlignment.Center); content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center); content.SetValue(ContentPresenter.MarginProperty,new TemplateBindingExtension(Control.PaddingProperty)); border.AppendChild(content); template.VisualTree=border;
        var selected=new Trigger { Property=IsDockActiveProperty,Value=true }; selected.Setters.Add(new Setter(Border.BackgroundProperty,BrushFor(42),"ButtonBorder")); selected.Setters.Add(new Setter(Border.BorderBrushProperty,BrushFor(110),"ButtonBorder")); selected.Setters.Add(new Setter(Border.BorderThicknessProperty,new Thickness(1),"ButtonBorder")); template.Triggers.Add(selected);
        var over=new Trigger { Property=UIElement.IsMouseOverProperty,Value=true }; over.Setters.Add(new Setter(Border.BackgroundProperty,BrushFor(48),"ButtonBorder")); template.Triggers.Add(over);
        var down=new Trigger { Property=ButtonBase.IsPressedProperty,Value=true }; down.Setters.Add(new Setter(Border.BackgroundProperty,BrushFor(69),"ButtonBorder")); template.Triggers.Add(down);
        var focus=new Trigger { Property=UIElement.IsKeyboardFocusedProperty,Value=true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty,BrushFor(156),"ButtonBorder")); focus.Setters.Add(new Setter(Border.BorderThicknessProperty,new Thickness(1),"ButtonBorder")); template.Triggers.Add(focus);
        style.Setters.Add(new Setter(Control.TemplateProperty,template)); style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0))); return style;
    }
    private static Style CreateFlatMenuItemStyle()
    {
        var style=new Style(typeof(MenuItem)); style.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.LightGray)); style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent)); style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,7,16,7)));
        var template=new ControlTemplate(typeof(MenuItem)); var border=new FrameworkElementFactory(typeof(Border),"MenuBorder"); border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
        var content=new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(ContentPresenter.ContentSourceProperty,"Header"); content.SetValue(ContentPresenter.RecognizesAccessKeyProperty,true); border.AppendChild(content); template.VisualTree=border;
        var hover=new Trigger { Property=MenuItem.IsHighlightedProperty,Value=true }; hover.Setters.Add(new Setter(Border.BackgroundProperty,BrushFor(52),"MenuBorder")); template.Triggers.Add(hover);
        var pressed=new Trigger { Property=IsMenuPressedProperty,Value=true }; pressed.Setters.Add(new Setter(Border.BackgroundProperty,BrushFor(72),"MenuBorder")); template.Triggers.Add(pressed);
        var focus=new Trigger { Property=UIElement.IsKeyboardFocusedProperty,Value=true }; focus.Setters.Add(new Setter(Border.BackgroundProperty,BrushFor(52),"MenuBorder")); template.Triggers.Add(focus);
        style.Setters.Add(new Setter(Control.TemplateProperty,template)); return style;
    }
    private static ControlTemplate CreateContextMenuTemplate()
    {
        var template=new ControlTemplate(typeof(ContextMenu)); var border=new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty,BrushFor(30)); border.SetValue(Border.BorderBrushProperty,BrushFor(72)); border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        var content=new FrameworkElementFactory(typeof(ItemsPresenter)); content.SetValue(ItemsPresenter.MarginProperty,new Thickness(2)); border.AppendChild(content); template.VisualTree=border; return template;
    }

    private void ShowAddAppDialog()
    {
        _pendingIconFile=null; 
        CreateAddAppDialog().ShowDialog();
    }

    private Window CreateAddAppDialog()
    {
        var dialog=new Window { Title=$"Add App · {_brandName}", Width=390, Height=370, ResizeMode=ResizeMode.NoResize, WindowStartupLocation=WindowStartupLocation.CenterOwner, Owner=this, Background=BrushFor(28), Foreground=Brushes.White, ShowInTaskbar=false };
        _addAppDialog=dialog;
        dialog.Closed+=(_,_)=>{ if(ReferenceEquals(_addAppDialog,dialog)) _addAppDialog=null; };
        var grid=new Grid { Margin=new Thickness(20) }; for(var i=0;i<6;i++) grid.RowDefinitions.Add(new RowDefinition { Height=i==3?new GridLength(1,GridUnitType.Star):GridLength.Auto });
        grid.Children.Add(new TextBlock { Text="Add an app to your dock", FontSize=18, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,0,0,14) });
        var name=new TextBox { Margin=new Thickness(0,4,0,8), Height=32, Padding=new Thickness(8), Background=BrushFor(42), Foreground=Brushes.White, BorderBrush=BrushFor(76), ToolTip="App name" };
        var nameField=new StackPanel(); nameField.Children.Add(new TextBlock { Text="App name", FontSize=12, Foreground=BrushFor(180) }); nameField.Children.Add(name); Grid.SetRow(nameField,1); grid.Children.Add(nameField);
        var url=new TextBox { Margin=new Thickness(0,4,0,8), Height=32, Padding=new Thickness(8), Background=BrushFor(42), Foreground=Brushes.White, BorderBrush=BrushFor(76), ToolTip="Website address" };
        var urlField=new StackPanel(); urlField.Children.Add(new TextBlock { Text="Website address", FontSize=12, Foreground=BrushFor(180) }); urlField.Children.Add(url); Grid.SetRow(urlField,2); grid.Children.Add(urlField);
        var favicon=new RadioButton { Content="Use website favicon", IsChecked=true, Foreground=Brushes.LightGray, Margin=new Thickness(0,0,0,8) };
        var fileChoice=new RadioButton { Content="Choose an icon file…", Foreground=Brushes.LightGray, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(0,0,0,2) };
        var iconPath=new TextBlock { Foreground=BrushFor(180), Margin=new Thickness(22,2,0,0), TextTrimming=TextTrimming.CharacterEllipsis };
        var iconChoices=new StackPanel { Orientation=System.Windows.Controls.Orientation.Vertical, Margin=new Thickness(0,8,0,0) }; iconChoices.Children.Add(favicon); iconChoices.Children.Add(fileChoice); iconChoices.Children.Add(iconPath); Grid.SetRow(iconChoices,3); grid.Children.Add(iconChoices);
        var buttons=new StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment=System.Windows.HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Bottom };
        var cancel=new WpfButton { Content="Cancel", Width=82, Height=32, Margin=new Thickness(0,0,8,0), Style=FlatButtonStyle, Foreground=Brushes.LightGray, Background=Brushes.Transparent, BorderBrush=BrushFor(76) }; cancel.Click+=(_,_)=>dialog.DialogResult=false;
        var add=new WpfButton { Content="Add App", Width=88, Height=32, Style=FlatButtonStyle, Foreground=Brushes.White, Background=BrushFor(54), BorderBrush=BrushFor(76) };
        buttons.Children.Add(cancel); buttons.Children.Add(add); Grid.SetRow(buttons,5); grid.Children.Add(buttons);
        fileChoice.Checked+=(_,_)=>{ var picker=new Microsoft.Win32.OpenFileDialog { Title="Choose an app icon", Filter="Images|*.png;*.jpg;*.jpeg;*.ico;*.bmp|All files|*.*" }; if(picker.ShowDialog(dialog)==true){_pendingIconFile=picker.FileName;iconPath.Text=Path.GetFileName(picker.FileName);} else favicon.IsChecked=true; };
        add.Click+=async (_,_)=>{
            try { var id=await AddLauncherFromFormAsync(name.Text,url.Text,_pendingIconFile,favicon.IsChecked==true); if(id is not null) dialog.DialogResult=true; }
            catch(Exception e) { System.Windows.MessageBox.Show(dialog,e.Message,"Add App",MessageBoxButton.OK,MessageBoxImage.Information); }
        };
        dialog.Content=grid; dialog.Loaded+=(_,_)=> { if(!_uiTest) name.Focus(); }; return dialog;
    }

    private async Task<string?> AddLauncherFromFormAsync(string name,string url,string? iconFile,bool useFavicon)
    {
        if(!useFavicon && string.IsNullOrWhiteSpace(iconFile)) throw new FormatException("Choose an icon file or use the website favicon.");
        var selectedIcon=useFavicon ? null : iconFile;
        if(!TryAddLauncher(name,url,selectedIcon,out var error)) throw new FormatException(error);
        return ConfiguredLaunchers.Last().Id;
    }

    private static async Task<string?> TrySaveWebsiteFaviconAsync(string id,string url,string directory)
    {
        try {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var client=new HttpClient { Timeout=TimeSpan.FromSeconds(2) };
            var origin=new Uri(url).GetLeftPart(UriPartial.Authority); using var response=await client.GetAsync(origin+"/favicon.ico",HttpCompletionOption.ResponseHeadersRead,cts.Token);
            if(!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 1_000_000) return null;
            await using var source=await response.Content.ReadAsStreamAsync(cts.Token); await using var buffer=new MemoryStream(); var chunk=new byte[8192];
            while(true) { var read=await source.ReadAsync(chunk,cts.Token); if(read==0) break; if(buffer.Length+read>1_000_000)return null; await buffer.WriteAsync(chunk.AsMemory(0,read),cts.Token); }
            var bytes=buffer.ToArray(); if(bytes.Length==0)return null;
            Directory.CreateDirectory(directory); var path=id+".ico"; await File.WriteAllBytesAsync(Path.Combine(directory,path),bytes,cts.Token); return path;
        } catch { return null; }
    }

    internal object? CaptureDock(string path) => CaptureElement(_dock,path);
    internal object? CaptureSettingsMenu(string path)
    {
        var settings=_bottomButtons.Children.OfType<WpfButton>().LastOrDefault(); if(settings?.ContextMenu is not { } menu) return null;
        menu.PlacementTarget=settings; menu.Placement=PlacementMode.Right; menu.IsOpen=true; menu.UpdateLayout(); Dispatcher.Invoke(DispatcherPriority.Render,()=>{}); var result=CaptureElement(menu,path); menu.IsOpen=false; return result;
    }
    internal object? CaptureAddApp(string path)
    {
        _pendingIconFile=null; 
        var dialog=CreateAddAppDialog(); dialog.ShowActivated=false; dialog.Show(); Dispatcher.Invoke(DispatcherPriority.Render,()=>{}); var r=CaptureElement(dialog,path); dialog.Close(); return r;
    }
    private static object? CaptureElement(FrameworkElement element,string path)
    {
        try { element.UpdateLayout(); var width=(int)Math.Ceiling(element.ActualWidth); var height=(int)Math.Ceiling(element.ActualHeight); if(width<=0||height<=0)return null; var bmp=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bmp.Render(element); using var fs=File.Create(path); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); encoder.Save(fs); return new { path,width,height }; } catch { return null; }
    }
    internal object UiDockMetrics()
    {
        var gear=_bottomButtons.Children.OfType<WpfButton>().LastOrDefault(); var gearPoint=gear is null?new WpfPoint():gear.TranslatePoint(new WpfPoint(0,gear.ActualHeight),_dock); var settings=gear is not null && gear.ActualHeight>0 && Math.Abs(_dock.ActualHeight-gearPoint.Y)<8;
        return new { settingsAtBottom=settings, hasAccountDivider=_appButtons.Children.OfType<FrameworkElement>().Any(x=>Equals(x.Tag,"account-divider")), appButtonCount=_launcherButtons.Count, iconOrder=ConfiguredLaunchers.Select(x=>x.Id).ToArray(), colorHoverAssetsAvailable=new[]{"gmail","calendar","soundcloud"}.All(x=>LoadIcon(x+"-color-128.png") is not null) };
    }
}
