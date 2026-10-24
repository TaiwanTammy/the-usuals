namespace SidebarPrototype;

/// <summary>Reads the hidden test switches. Compiled only into the TestHarness build.</summary>
internal static class TestHarnessLaunch
{
    public static LaunchOptions Parse(string[] args)
    {
        var dataDirArg = Array.FindIndex(args, x => x == "--data-dir");
        var dataDirectory = dataDirArg >= 0 && dataDirArg + 1 < args.Length ? args[dataDirArg + 1] : null;
        var smokeTest = args.Contains("--smoke-test");
        var layoutTest = args.Contains("--layout-test");
        var transitionTest =
            args.Contains("--transition-test")
            || args.Contains("--transition-native-test")
            || args.Contains("--transition-native-toolwindow-test");
        var transitionNative =
            args.Contains("--transition-native-test") || args.Contains("--transition-native-toolwindow-test");
        var transitionToolWindow = args.Contains("--transition-native-toolwindow-test");
        var unloadOnlyTest = args.Contains("--unload-only-test");
        var uiTest = args.Contains("--ui-test");
        var fixture =
            args.Contains("--diagnostics")
            || smokeTest
            || args.Contains("--browser-test")
            || layoutTest
            || unloadOnlyTest
            || transitionTest
            || uiTest;
        return new LaunchOptions
        {
            DataDirectory = dataDirectory,
            QuietSecondInstance = smokeTest || transitionTest || uiTest,
            DiagnosticStartup = smokeTest || layoutTest || transitionTest || uiTest,
            Window = new HostWindowOptions
            {
                Fixture = fixture,
                Smoke = smokeTest || layoutTest || transitionTest || unloadOnlyTest || uiTest,
                BrowserOnly = args.Contains("--browser-test") || uiTest,
                PrepareBrowsers = !args.Contains("--no-prewarm") && !transitionTest && !uiTest,
                LayoutOnly = layoutTest,
                TransitionTest = transitionTest,
                TransitionNative = transitionNative,
                TransitionToolWindow = transitionToolWindow,
                UnloadOnlyTest = unloadOnlyTest,
                UiTest = uiTest,
            },
        };
    }
}
