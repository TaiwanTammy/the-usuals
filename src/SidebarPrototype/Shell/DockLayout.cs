namespace SidebarPrototype.Shell;

/// <summary>Logical (WPF device-independent) rectangles for the dock and adjacent panel.</summary>
public sealed record DockLayout(System.Windows.Rect DockBounds, System.Windows.Rect PanelBounds);
