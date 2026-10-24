using System.Windows;
using System.Windows.Controls;

namespace SidebarPrototype;

internal sealed class ShortcutDialog : Window
{
    public uint Modifiers { get; private set; }
    public uint VirtualKey { get; private set; }

    public ShortcutDialog()
    {
        Title = "Set sidebar shortcut";
        Width = 380;
        Height = 140;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Content = new TextBlock
        {
            Text = "Press a key combination (for example Ctrl + Alt + Space). Press Escape to cancel.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
            VerticalAlignment = VerticalAlignment.Center,
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                DialogResult = false;
                return;
            }
            if (
                e.Key
                is System.Windows.Input.Key.LeftCtrl
                    or System.Windows.Input.Key.RightCtrl
                    or System.Windows.Input.Key.LeftAlt
                    or System.Windows.Input.Key.RightAlt
                    or System.Windows.Input.Key.LeftShift
                    or System.Windows.Input.Key.RightShift
                    or System.Windows.Input.Key.LWin
                    or System.Windows.Input.Key.RWin
            )
                return;
            var state = System.Windows.Input.Keyboard.Modifiers;
            uint mods = 0x4000;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Alt))
                mods |= 0x0001;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Control))
                mods |= 0x0002;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                mods |= 0x0004;
            if (state.HasFlag(System.Windows.Input.ModifierKeys.Windows))
                mods |= 0x0008;
            if ((mods & 0x000F) == 0)
                return;
            Modifiers = mods;
            VirtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key);
            e.Handled = true;
            DialogResult = true;
        };
    }
}
