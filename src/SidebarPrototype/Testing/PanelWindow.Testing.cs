using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;

namespace SidebarPrototype;

internal sealed partial class PanelWindow
{
    public int WindowPositionMessages { get; private set; }
    public int WindowMoveMessages { get; private set; }
    public int WindowSizeMessages { get; private set; }

    private void RecordTransitionMessage(nint hwnd, int message, nint lParam)
    {
        if (message == 0x0046)
            _host.RecordNativeBounds("panel", hwnd, message, lParam);
        else if (message == 0x0047)
        {
            WindowPositionMessages++;
            _host.RecordNativeBounds("panel", hwnd, message);
        }
        else if (message == 0x0003)
        {
            WindowMoveMessages++;
            _host.RecordNativeBounds("panel", hwnd, message);
        }
        else if (message == 0x0005)
        {
            WindowSizeMessages++;
            _host.RecordNativeBounds("panel", hwnd, message);
        }
    }

    internal object CaptureToolbar(string path, double width)
    {
        var original = Width;
        Width = width;
        if (_toolbar is null)
            throw new InvalidOperationException("Toolbar is unavailable");
        UpdateLayout();
        _toolbar.UpdateLayout();
        UpdateCompactToolbar();
        UpdateLayout();
        _toolbar.UpdateLayout();
        var height = _toolbar.ActualHeight;
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(_toolbar.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(height)),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32
        );
        bitmap.Render(_toolbar);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        var commands = _toolbarButtons
            .Where(p => p.Value.Visibility == Visibility.Visible)
            .Select(p => p.Key)
            .ToArray();
        var overflowCommands =
            _overflowMenu
                ?.Items.OfType<MenuItem>()
                .Select(i => i.Tag?.ToString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray()
            ?? [];
        var noDuplicates = !commands.Intersect(overflowCommands, StringComparer.Ordinal).Any();
        var noOverflow = _toolbarButtons
            .Values.Where(b => b.Visibility == Visibility.Visible)
            .All(b =>
            {
                var origin = b.TransformToAncestor(_toolbar).Transform(new Point(0, 0));
                var end = b.TransformToAncestor(_toolbar).Transform(new Point(b.ActualWidth, b.ActualHeight));
                return origin.X >= -0.5
                    && origin.Y >= -0.5
                    && end.X <= _toolbar.ActualWidth + 0.5
                    && end.Y <= _toolbar.ActualHeight + 0.5;
            });
        var visibleControls = new[]
        {
            _modeButton,
            _browserButton,
            _deviceButton,
            _topmostButton,
            _zoomOutButton,
            _zoomInButton,
            _themeButton,
            _moreButton,
            _closeButton,
        }.All(x => x is { Visibility: Visibility.Visible } && x.ActualWidth > 0 && x.ActualHeight > 0);
        var result = new
        {
            toolbarWidth = _toolbar.ActualWidth,
            toolbarHeight = height,
            visibleControls,
            commands,
            overflowcommands = overflowCommands,
            noDuplicates,
            noOverflow,
            compact = _toolbar.ActualWidth <= 360,
        };
        Width = original;
        UpdateLayout();
        UpdateCompactToolbar();
        return result;
    }
}
