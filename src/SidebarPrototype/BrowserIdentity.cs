using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace SidebarPrototype;

/// <summary>Applies a mobile or desktop browser identity and its matching viewport metrics.</summary>
public static partial class BrowserIdentity
{
    private static readonly Regex ChromiumVersionPattern = new(@"\bChrome/(?<version>\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EdgeVersionPattern = new(@"\bEdg/(?<version>\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task ApplyAsync(CoreWebView2 core, bool mobile, string nativeDesktopUserAgent,int width=500,int height=800)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDesktopUserAgent);

        var chromiumVersion = ExtractVersion(ChromiumVersionPattern, nativeDesktopUserAgent, "Chromium");
        var edgeMatch = EdgeVersionPattern.Match(nativeDesktopUserAgent);
        var edgeVersion = edgeMatch.Success
            ? edgeMatch.Groups["version"].Value
            : chromiumVersion;
        var userAgent = mobile
            ? $"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromiumVersion} Mobile Safari/537.36"
            : nativeDesktopUserAgent;

        // WebView2's UserAgent setting controls the string, while Chromium's client-hint
        // metadata is separate. Set both so requests and navigator.userAgentData agree;
        // relying on the setting alone can clear UAData and leave service workers stale.
        core.Settings.UserAgent = userAgent;
        var metadata = CreateMetadata(chromiumVersion, edgeVersion, mobile, isEdge: !mobile && edgeMatch.Success);
        var parameters = JsonSerializer.Serialize(new { userAgent, platform=mobile ? "Linux armv8l" : "Win32", userAgentMetadata = metadata });
        await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", parameters);
        await ApplyViewportAsync(core,mobile,width,height);
    }
    public static Task ApplyViewportAsync(CoreWebView2 core,bool mobile,int width,int height) => mobile
        ? core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",JsonSerializer.Serialize(new { width=Math.Max(320,width),height=Math.Max(320,height),deviceScaleFactor=0,mobile=true,screenWidth=Math.Max(320,width),screenHeight=Math.Max(320,height) }))
        : core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride","{}");

    private static string ExtractVersion(Regex pattern, string userAgent, string browser)
    {
        var match = pattern.Match(userAgent);
        if (!match.Success) throw new ArgumentException($"The native user agent does not contain a {browser} version.", nameof(userAgent));
        return match.Groups["version"].Value;
    }

    private static object CreateMetadata(string chromiumVersion, string edgeVersion, bool mobile, bool isEdge)
    {
        var chromiumMajor = Major(chromiumVersion);
        var browserBrand = isEdge ? "Microsoft Edge" : "Google Chrome";
        var browserVersion = isEdge ? edgeVersion : chromiumVersion;
        var brands = new[]
        {
            new { brand = "Not A(Brand", version = "24" },
            new { brand = "Chromium", version = chromiumMajor },
            new { brand = browserBrand, version = Major(browserVersion) }
        };
        var fullVersionList = new[]
        {
            new { brand = "Not A(Brand", version = "24.0.0.0" },
            new { brand = "Chromium", version = chromiumVersion },
            new { brand = browserBrand, version = browserVersion }
        };

        return new
        {
            brands,
            fullVersionList,
            fullVersion = chromiumVersion,
            platform = mobile ? "Android" : "Windows",
            platformVersion = mobile ? "10.0.0" : "10.0.0",
            architecture = mobile ? "" : "x86",
            model = mobile ? "K" : "",
            mobile,
            bitness = mobile ? "" : "64",
            wow64 = false,
            formFactors = new[] { mobile ? "Mobile" : "Desktop" }
        };
    }

    private static string Major(string version) => version.Split('.')[0];
}
