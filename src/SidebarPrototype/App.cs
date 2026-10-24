using System.Text.Json;
using System.Windows;
using SidebarPrototype.Shell;

namespace SidebarPrototype;

public sealed class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    [STAThread]
    public static int Main(string[] args)
    {
#if TEST_HARNESS
        if (args.Contains("--self-test"))
            return SelfTests.Run();
        var launch = TestHarnessLaunch.Parse(args);
#else
        // The release build ignores every command-line switch; they all belong to the test harness.
        var launch = LaunchOptions.Default;
#endif
        ShellDpi.EnablePerMonitorV2();
        var app = new App();
        app.Startup += (_, _) =>
        {
            try
            {
                app.Start(launch);
            }
            catch (Exception e)
            {
                app.ReportStartupFailure(e, launch);
                app.Shutdown(1);
            }
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            app.ReportStartupFailure(e.Exception, launch);
            (app.MainWindow as HostWindow)?.EmergencyCleanup();
            e.Handled = true;
            app.Shutdown(1);
        };
        return app.Run();
    }

    private void Start(LaunchOptions launch)
    {
        var isolated = launch.DataDirectory is not null;
        var fixture = launch.Window.Fixture;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SidebarPrototype"
        );
        var root = isolated
            ? Path.GetFullPath(launch.DataDirectory!)
            : Path.Combine(appData, fixture ? "diagnostics" : "");
        Directory.CreateDirectory(root);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var mutexName =
            "Local\\SidebarPrototype.Singleton."
            + (
                isolated || fixture
                    ? Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))
                    )[..12]
                    : "user"
            );
        _instanceMutex = new Mutex(true, mutexName, out var owner);
        if (!owner)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            if (!launch.QuietSecondInstance)
                System.Windows.MessageBox.Show(
                    "The sidebar is already running. Quit the current copy using its tray menu, then open this version.",
                    "Sidebar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            Shutdown(launch.QuietSecondInstance ? 2 : 0);
            return;
        }
        _ownsMutex = true;
        Exit += (_, _) =>
        {
            if (_ownsMutex)
                _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        };
        var settings = AppSettings.SafeLoad(Path.Combine(root, "settings.json"));
        try
        {
            var win = new HostWindow(root, settings, launch.Window);
            MainWindow = win;
            win.Show();
        }
        catch
        {
            if (_ownsMutex)
            {
                _instanceMutex?.ReleaseMutex();
                _ownsMutex = false;
                _instanceMutex?.Dispose();
                _instanceMutex = null;
            }
            throw;
        }
    }

    private void ReportStartupFailure(Exception error, LaunchOptions launch)
    {
        var directory = launch.DataDirectory is not null
            ? Path.GetFullPath(launch.DataDirectory)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SidebarPrototype"
            );
        var diagnostic = launch.DiagnosticStartup;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "startup-error.log"),
                $"{DateTimeOffset.UtcNow:O} {error.GetType().FullName}{Environment.NewLine}{error.StackTrace}"
            );
            if (diagnostic)
                File.WriteAllText(
                    Path.Combine(directory, "startup-error-evidence.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            mode = "startup-diagnostic",
                            status = "failed",
                            error = error.GetType().Name,
                        }
                    )
                );
        }
        catch { }
        if (!diagnostic)
            System.Windows.MessageBox.Show(
                "The sidebar could not start. Details were saved to the local diagnostic log.",
                AppBranding.TheUsuals,
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
    }
}
