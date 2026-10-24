using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace SidebarPrototype;

/// <summary>A browser popup hosted inside the existing panel, with no OS window to drag or snap.</summary>
internal sealed class AuthSurface : ContentControl
{
    public const string WindowCloseMessage = "sidebar-auth-surface-close";
    private readonly DockPanel _root = new();
    private readonly DockPanel _header = new() { Background=System.Windows.Media.Brushes.Black };
    private readonly TextBlock _location = new() { Padding=new Thickness(8), Foreground=System.Windows.Media.Brushes.White,TextWrapping=TextWrapping.Wrap };
    public string LauncherId { get; }
    public WebView2? Browser { get; private set; }
    public bool IsPromoted { get; private set; }
    public bool MainHandlersReady { get; set; }
    public bool IsClosed { get; private set; }
    public event EventHandler? Closed;
    public AuthSurface(Launcher launcher)
    {
        LauncherId=launcher.Id; var cancel=new WpfButton { Content="Cancel sign-in",Margin=new Thickness(4),Padding=new Thickness(6) };
        cancel.Click+=(_,_)=>Close(); DockPanel.SetDock(cancel,Dock.Right); _header.Children.Add(cancel); _header.Children.Add(_location);
        DockPanel.SetDock(_header,Dock.Top); _root.Children.Add(_header); Content=_root;
    }
    public void SetBrowser(WebView2 view) { Browser=view; _root.Children.Add(view); }
    public void MarkPromoted() { IsPromoted=true; _header.Visibility=Visibility.Collapsed; }
    public void Close() { if(IsPromoted || IsClosed) return; IsClosed=true; Closed?.Invoke(this,EventArgs.Empty); }
    public void SetLocation(string? source,string status)
    {
        var origin=UrlPolicy.TryParseHttp(source,out var uri) ? uri!.GetLeftPart(UriPartial.Authority) : "Sign-in";
        _location.Text=string.IsNullOrEmpty(status) ? origin : $"{origin}\n{status}";
    }
}
