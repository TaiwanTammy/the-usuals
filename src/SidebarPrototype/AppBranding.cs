namespace SidebarPrototype;

public enum BrandingMode { Rotate, TheUsuals, PaneInTheApps }

public sealed record BrandingSelection(string Name, int LastBrandIndex);

public static class AppBranding
{
    public const string TheUsuals = "The Usuals";
    public const string PaneInTheApps = "Pane in the Apps";

    public static BrandingSelection Select(BrandingMode mode, int lastBrandIndex)
    {
        if (mode == BrandingMode.TheUsuals) return new(TheUsuals, lastBrandIndex is 0 or 1 ? lastBrandIndex : -1);
        if (mode == BrandingMode.PaneInTheApps) return new(PaneInTheApps, lastBrandIndex is 0 or 1 ? lastBrandIndex : -1);
        var next = lastBrandIndex == 0 ? 1 : 0;
        return new(next == 0 ? TheUsuals : PaneInTheApps, next);
    }
}
