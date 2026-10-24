namespace SidebarPrototype;

/// <summary>
/// How a host window runs. A normal launch uses the defaults; only the test build
/// (see Testing/TestHarnessLaunch.cs) turns any of the fixture or regression modes on.
/// </summary>
internal sealed record HostWindowOptions
{
    /// <summary>Serve local fixture pages instead of the real web apps.</summary>
    public bool Fixture { get; init; }

    /// <summary>Run a scripted regression on load and quit with its result.</summary>
    public bool Smoke { get; init; }

    /// <summary>Keep windows off-screen and never reserve desktop space.</summary>
    public bool BrowserOnly { get; init; }

    /// <summary>Create empty browser views in the background after startup.</summary>
    public bool PrepareBrowsers { get; init; } = true;

    public bool LayoutOnly { get; init; }
    public bool TransitionTest { get; init; }
    public bool TransitionNative { get; init; }
    public bool TransitionToolWindow { get; init; }
    public bool UnloadOnlyTest { get; init; }
    public bool UiTest { get; init; }
}

/// <summary>Everything the command line can change about startup.</summary>
internal sealed record LaunchOptions
{
    public static LaunchOptions Default { get; } = new();

    /// <summary>An isolated data folder instead of %LOCALAPPDATA%\SidebarPrototype.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Exit with code 2 instead of showing a dialog when another copy is running.</summary>
    public bool QuietSecondInstance { get; init; }

    /// <summary>Write startup failures as evidence JSON instead of showing a dialog.</summary>
    public bool DiagnosticStartup { get; init; }

    public HostWindowOptions Window { get; init; } = new();
}
