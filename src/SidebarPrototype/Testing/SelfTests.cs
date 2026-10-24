namespace SidebarPrototype;

internal sealed class SelfTests
{
    public static int Run()
    {
        try
        {
            var gmail = Launchers.All[0];
            Assert(
                UrlPolicy.TryParseHttp("https://mail.google.com/", out var u) && UrlPolicy.IsInternal(u!, gmail),
                "recognized app host"
            );
            Assert(!UrlPolicy.TryParseHttp("javascript:alert(1)", out _), "reject non-http");
            Assert(
                !UrlPolicy.TryParseHttp("https://mail.google.com.evil.test/", out var evil)
                    || !UrlPolicy.IsInternal(evil!, gmail),
                "host boundary"
            );
            Assert(!UrlPolicy.TryParseHttp("https://user:pass@mail.google.com/", out _), "reject credentials");
            Assert(
                UrlPolicy.IsAllowedRestore("https://mail.google.com/mail/u/0/", gmail)
                    && !UrlPolicy.IsAllowedRestore("https://mail.google.com/oauth/callback?code=x", gmail)
                    && !UrlPolicy.IsSafeRestorable("https://mail.google.com/#state=x")
                    && !UrlPolicy.IsSafeRestorable("https://mail.google.com/?%63ode=one-time")
                    && !UrlPolicy.IsSafeRestorable("https://mail.google.com/#%61ccess_token=secret"),
                "restore URL filtering"
            );
            Assert(
                Launchers.All[0].Profile == Launchers.All[1].Profile
                    && Launchers.All[0].Profile != Launchers.All[2].Profile,
                "profile mapping"
            );
            Assert(PanelSizing.Initial(1440) == 480 && PanelSizing.Clamp(900, 800, 128) <= 352, "panel sizing");
            Assert(PanelSizing.Clamp(900, 350, 128) <= 222, "narrow sizing");
            Assert(
                AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com/signin/v2/identifier"))
                    && AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com/o/oauth2/v2/auth"))
                    && AppReturnPolicy.IsGoogleSignInIntent(
                        new Uri("https://accounts.google.com/AccountChooser?continue=https%3A%2F%2Fcalendar.google.com")
                    )
                    && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com/AccountChooserEvil"))
                    && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://accounts.google.com.evil.example/signin"))
                    && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("http://accounts.google.com/signin"))
                    && !AppReturnPolicy.IsGoogleSignInIntent(new Uri("https://www.google.com/signin")),
                "bounded Google popup auth intent"
            );
            var state = new ViewState();
            var old = state.BeginSwitch("gmail-a");
            var latest = state.BeginSwitch("calendar-b");
            Assert(!state.CanPresent("gmail-a", old) && state.CanPresent("calendar-b", latest), "switch race");
            state.Hide();
            Assert(!state.CanPresent("calendar-b", latest), "hide race");
            var test = new AppSettings
            {
                DockWidth = double.NaN,
                Launchers = new()
                {
                    ["x"] = new()
                    {
                        Zoom = double.PositiveInfinity,
                        Width = -1,
                        LastUrl = "javascript:bad",
                    },
                },
            }.Validate();
            Assert(
                test.DockWidth == 45
                    && test.Launchers["x"].Zoom == 1
                    && test.Launchers["x"].Width == 320
                    && test.Launchers["x"].LastUrl is null
                    && new AppSettings { Launchers = new() { ["x"] = new() } }
                        .Validate()
                        .Launchers["x"]
                        .Width == 0,
                "config sanitation"
            );
            Console.WriteLine(
                "PASS: URL policy, host boundary, config sanitation, sizing, profile mapping, view race state"
            );
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("FAIL: " + e.Message);
            return 1;
        }
    }

    private static void Assert(bool ok, string name)
    {
        if (!ok)
            throw new InvalidOperationException(name);
    }
}
