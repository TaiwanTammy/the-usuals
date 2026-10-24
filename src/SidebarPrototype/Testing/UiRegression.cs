using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using SidebarPrototype.Shell;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    [DllImport("user32.dll", EntryPoint="GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiGetClientRect(nint hwnd,out NativeMethods.Rect rect);
    [DllImport("user32.dll", EntryPoint="ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiClientToScreen(nint hwnd,ref NativeMethods.Point point);
    [DllImport("user32.dll", EntryPoint="PrintWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiPrintWindow(nint hwnd,nint hdc,uint flags);
    [DllImport("user32.dll", EntryPoint="SendMessageW")]
    private static extern nint UiSendMessage(nint hwnd,uint message,nint wParam,nint lParam);

    private static bool CaptureOwnedNativeWindow(Window window,string path)
    {
        var hwnd=new WindowInteropHelper(window).Handle;
        if(!NativeMethods.GetWindowRect(hwnd,out var rect)) return false;
        using var bitmap=new System.Drawing.Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top);
        using var graphics=System.Drawing.Graphics.FromImage(bitmap);
        var dc=graphics.GetHdc(); bool captured;
        try { captured=UiPrintWindow(hwnd,dc,2); } finally { graphics.ReleaseHdc(dc); }
        if(captured) bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
        return captured;
    }

    private static bool IsGrayscale(BitmapSource source)
    {
        var image=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        var bytes=new byte[image.PixelWidth*image.PixelHeight*4]; image.CopyPixels(bytes,image.PixelWidth*4,0);
        for(var i=0;i<bytes.Length;i+=4) if(bytes[i+3]>32 && (Math.Abs(bytes[i]-bytes[i+1])>1 || Math.Abs(bytes[i+1]-bytes[i+2])>1)) return false;
        return true;
    }

    private bool IconHasGrayscaleAndColor(string id)
    {
        var button=_launcherButtons.Single(x=>Equals(x.Tag,id));
        if(button.Content is not Grid grid) return false;
        var gray=grid.Children.OfType<Image>().SingleOrDefault(x=>Equals(x.Tag,"icon-grayscale"));
        var color=grid.Children.OfType<Image>().SingleOrDefault(x=>Equals(x.Tag,"icon-color"));
        if(gray?.Source is not BitmapSource graySource || color?.Source is not BitmapSource colorSource || !IsGrayscale(graySource) || IsGrayscale(colorSource)) return false;
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount) { RoutedEvent=Mouse.MouseEnterEvent });
        var revealed=color.Opacity==1;
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount) { RoutedEvent=Mouse.MouseLeaveEvent });
        return revealed && color.Opacity==0;
    }

    private async Task RunUiRegressionAsync()
    {
        var results=new Dictionary<string,object?> { ["mode"]="isolated-ui-regression",["startedUtc"]=DateTimeOffset.UtcNow };
        void Check(string name,bool passed,object? details=null) { results[name]=passed; if(details is not null) results[name+"Details"]=details; if(!passed) throw new InvalidOperationException(name); }
        try {
            var original=ConfiguredLaunchers.ToArray();
            var app=original[0];
            await ActivateAsync(app,false);
            await WaitDocumentAsync(_views[app.Id]);
            var retained=_views.ToDictionary(x=>x.Key,x=>x.Value);
            var initialSource=_views[app.Id].Source;

            var previousNavCount=0;
            foreach(var width in new[]{320d,500d,800d}) {
                var toolbar=_panel.CaptureToolbar(Path.Combine(_root,$"toolbar-{width:0}.png"),width);
                var json=JsonSerializer.SerializeToElement(toolbar);
                Check($"toolbar{width:0}Fits",json.GetProperty("noOverflow").GetBoolean(),toolbar);
                Check($"toolbar{width:0}HasNoDuplicates",json.GetProperty("noDuplicates").GetBoolean());
                var commands=json.GetProperty("commands").EnumerateArray().Select(x=>x.GetString()!).ToArray();
                var overflow=json.GetProperty("overflowcommands").EnumerateArray().Select(x=>x.GetString()!).ToArray();
                Check($"toolbar{width:0}RequiredActionsDirect",new[]{"mode","external","identity","top","zoom-","zoom+","theme","close"}.All(commands.Contains) && json.GetProperty("visibleControls").GetBoolean());
                Check($"toolbar{width:0}NavigationAvailableExactlyOnce",new[]{"back","forward","reload"}.All(x=>commands.Concat(overflow).Count(y=>y==x)==1));
                var navCount=new[]{"back","forward","reload"}.Count(commands.Contains);
                Check($"toolbar{width:0}PromotesActions",navCount>=previousNavCount && (width<800 || navCount==3)); previousNavCount=navCount;
                results[$"overflow{width:0}"]=_panel.CaptureOverflow(Path.Combine(_root,$"overflow-{width:0}.png"),width);
            }
            results["panel"]=_panel.CapturePanel(Path.Combine(_root,"panel.png"),500);
            results["dock"]=CaptureDock(Path.Combine(_root,"dock.png"));
            var dockMetrics=UiDockMetrics(); results["dockMetrics"]=dockMetrics;
            var dockJson=JsonSerializer.SerializeToElement(dockMetrics);
            Check("settingsAnchoredAtBottom",dockJson.GetProperty("settingsAtBottom").GetBoolean());
            Check("accountDividerPresent",dockJson.GetProperty("hasAccountDivider").GetBoolean());
            Check("officialIconsRevealRealColor",original.All(x=>IconHasGrayscaleAndColor(x.Id)));
            results["settingsMenu"]=CaptureSettingsMenu(Path.Combine(_root,"settings-menu.png"));
            results["addApp"]=CaptureAddApp(Path.Combine(_root,"add-app.png"));

            var hwnd=new WindowInteropHelper(_panel).Handle;
            var rectOk=NativeMethods.GetWindowRect(hwnd,out var outer) && UiGetClientRect(hwnd,out var client);
            var origin=new NativeMethods.Point();
            var mapped=UiClientToScreen(hwnd,ref origin);
            UiGetClientRect(hwnd,out var clientBounds);
            Check("nativePanelClientHasNoFrameInset",rectOk && mapped && origin.X==outer.Left && origin.Y==outer.Top && clientBounds.Right-clientBounds.Left==outer.Right-outer.Left && clientBounds.Bottom-clientBounds.Top==outer.Bottom-outer.Top,
                new { outer=new { outer.Left,outer.Top,outer.Right,outer.Bottom },client=new { origin.X,origin.Y,width=clientBounds.Right-clientBounds.Left,height=clientBounds.Bottom-clientBounds.Top } });
            results["ownedNativePanelCaptureAvailable"]=CaptureOwnedNativeWindow(_panel,Path.Combine(_root,"native-panel.png"));
            _panel.Width=550; _panel.UpdateLayout();
            UiSendMessage(hwnd,0x0232,0,0);
            Check("productionResizeHookCommitsWidth",Math.Abs(GetSetting(app.Id).Width-550)<1 && Math.Abs(_openWidth-550)<1,new { saved=GetSetting(app.Id).Width,open=_openWidth });
            NativeMethods.GetWindowRect(hwnd,out var resizedRect);
            var hitPoint=new nint(((resizedRect.Top+100)&0xffff)<<16 | ((resizedRect.Right-1)&0xffff));
            var edgeHit=UiSendMessage(hwnd,0x0084,0,hitPoint).ToInt64();
            Check("rightEdgeRemainsResizable",edgeHit==11,new { hitTest=edgeHit });

            var invalidAdded=TryAddLauncher("Unsafe","javascript:alert(1)",null,out _);
            var credentialsAdded=TryAddLauncher("Unsafe","https://name:secret@example.test/",null,out _);
            Check("invalidAddDoesNotChangeCatalog",!invalidAdded && !credentialsAdded && ConfiguredLaunchers.Count==original.Length);
            Check("customAppAdded",TryAddLauncher("Research","https://example.test/",null,out var addError),addError);
            var firstCustom=ConfiguredLaunchers.Single(x=>original.All(o=>o.Id!=x.Id));
            var customIcon=Path.Combine(_root,"custom-input.png");
            using(var image=new System.Drawing.Bitmap(16,16)) { using var graphics=System.Drawing.Graphics.FromImage(image); graphics.Clear(System.Drawing.Color.OrangeRed); image.Save(customIcon,System.Drawing.Imaging.ImageFormat.Png); }
            Check("customAppSecondAccountAdded",TryAddLauncher("Research 2","https://example.test/",customIcon,out addError),addError);
            var secondCustom=ConfiguredLaunchers.Last();
            Check("chosenCustomIconDisplaysAndChangesOnHover",IconHasGrayscaleAndColor(secondCustom.Id));
            Check("customProfilesAreStableAndIsolated",firstCustom.Id!=secondCustom.Id && firstCustom.Profile!=secondCustom.Profile && original.All(x=>x.Profile!=firstCustom.Profile && x.Profile!=secondCustom.Profile));

            var beforeDrop=ConfiguredLaunchers.Select(x=>x.Id).ToArray();
            Check("dockDropHandlerReorders",ExerciseDockDropForTest(beforeDrop[^1],beforeDrop[0]) && ConfiguredLaunchers[0].Id==beforeDrop[^1]);
            var requested=ConfiguredLaunchers.Select(x=>x.Id).Reverse().ToArray();
            ReorderLaunchers(requested);
            Check("catalogOrderMatchesRequest",ConfiguredLaunchers.Select(x=>x.Id).SequenceEqual(requested));
            Check("addAndReorderKeepLoadedPage",retained.All(x=>_views.TryGetValue(x.Key,out var current) && ReferenceEquals(x.Value,current)) && _views[app.Id].Source==initialSource && _state.ActiveId==app.Id && _panel.IsVisible);
            await ActivateAsync(secondCustom,false); await WaitDocumentAsync(_views[secondCustom.Id]);
            var oldMode=GetSetting(secondCustom.Id).Theme;
            OnToolbar("theme"); QueueZoom(secondCustom.Id,1.2);
            await ToggleIdentityAsync(secondCustom.Id,_views[secondCustom.Id]);
            await WaitDocumentAsync(_views[secondCustom.Id]);
            Check("customAppBrowserControlsUseItsOwnProfile",GetSetting(secondCustom.Id).Theme!=oldMode && GetSetting(secondCustom.Id).MobileMode && _views[secondCustom.Id].CoreWebView2.Profile.ProfileName==secondCustom.Profile);
            await _settingsWriter.FlushAsync();
            var loaded=AppSettings.SafeLoad(Path.Combine(_root,"settings.json"));
            var restored=LauncherCatalog.For(loaded);
            Check("customAppsAndOrderSurviveSettingsReload",restored.Select(x=>x.Id).SequenceEqual(requested) && restored.Single(x=>x.Id==firstCustom.Id).Profile==firstCustom.Profile && restored.Single(x=>x.Id==secondCustom.Id).StartUri==secondCustom.StartUri);
            Check("builtInAccountProfilesPreserved",original.All(x=>restored.Single(y=>y.Id==x.Id).Profile==x.Profile));
            var malformedFile=Path.Combine(_root,"malformed-custom-settings.json");
            File.WriteAllText(malformedFile,"""
                {"DockWidth":45,"Launchers":{"gmail-a":{"Zoom":1.4}},"LauncherOrder":[null,"gmail-a"],"CustomLaunchers":[{"Id":null,"Label":null,"Url":null,"Profile":null}]}
                """);
            var sanitized=AppSettings.SafeLoad(malformedFile);
            Check("badCustomDefinitionDoesNotDiscardExistingSettings",sanitized.Launchers.TryGetValue("gmail-a",out var preserved) && Math.Abs(preserved.Zoom-1.4)<.001 && LauncherCatalog.For(sanitized).Count==original.Length);
            results["status"]="completed";
        } catch(Exception error) {
            results["status"]="failed"; results["error"]=error.GetType().Name; results["message"]=error.Message; results["stack"]=error.StackTrace;
        } finally {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await _settingsWriter.FlushAsync();
            File.WriteAllText(Path.Combine(_root,"ui-evidence.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
            Quit();
        }
    }
}
