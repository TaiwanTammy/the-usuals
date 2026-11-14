using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace SidebarPrototype;

// The deploy light above the add button: one Railway service, amber while it builds (with minutes), green when live,
// red when the newest deploy failed. Click copies that deploy's logs. It reads deploy-watch.json in the data folder;
// with no file there is no light, and dropping the file in later makes it appear without a restart.
internal sealed class DeployWatchConfig
{
    public string Token { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsComplete => new[] { Token, ProjectId, EnvironmentId, ServiceId }.All(x => !string.IsNullOrWhiteSpace(x));
}

internal readonly record struct RailwayDeploy(string Id, string Status, DateTimeOffset CreatedAt, string Message);

internal static class RailwayDeploys
{
    public static bool IsActive(string s) => s is "QUEUED" or "WAITING" or "INITIALIZING" or "BUILDING" or "DEPLOYING";
    public static bool IsFailed(string s) => s is "FAILED" or "CRASHED";
    // Older deploys turn REMOVED once a newer one goes live; the newest deploy that isn't one of these is the one to show.
    public static bool Counts(string s) => s is not ("REMOVED" or "REMOVING" or "SKIPPED");

    public static string Word(string s) => s switch
    {
        "BUILDING" => "building",
        "DEPLOYING" => "deploying",
        "QUEUED" or "WAITING" or "INITIALIZING" => "starting",
        "FAILED" => "failed",
        "CRASHED" => "crashed",
        "SUCCESS" => "live",
        "SLEEPING" => "asleep",
        "NEEDS_APPROVAL" => "waiting for approval",
        _ => s.ToLowerInvariant(),
    };

    public static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "<1m" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m" : t.TotalHours < 24 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalDays}d";

    public static string Spoken(TimeSpan t)
    {
        static string N(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
        return t.TotalMinutes < 1 ? "under a minute" : t.TotalMinutes < 60 ? N((int)t.TotalMinutes, "minute") : t.TotalHours < 24 ? N((int)t.TotalHours, "hour") : N((int)t.TotalDays, "day");
    }

    public static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 90 ? line[..89] + "…" : line;
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    public static string Plain(string text) => Ansi.Replace(text, "");
}

internal sealed class RailwayClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly DeployWatchConfig _config;
    public RailwayClient(DeployWatchConfig config) => _config = config;

    private async Task<JsonElement> QueryAsync(string query, object variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://backboard.railway.com/graphql/v2")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Project-Access-Token", _config.Token);
        request.Headers.UserAgent.ParseAdd("SidebarPrototype");
        using var response = await Http.SendAsync(request, ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        if (json.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new InvalidOperationException(errors[0].TryGetProperty("message", out var m) ? m.GetString() : "Railway refused the request.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Railway answered {(int)response.StatusCode}.");
        return json.GetProperty("data");
    }

    public async Task<RailwayDeploy?> NewestAsync(CancellationToken ct)
    {
        var data = await QueryAsync(
            "query($i:DeploymentListInput!){deployments(input:$i,first:10){edges{node{id status createdAt meta}}}}",
            new { i = new { projectId = _config.ProjectId, environmentId = _config.EnvironmentId, serviceId = _config.ServiceId } }, ct);
        foreach (var edge in data.GetProperty("deployments").GetProperty("edges").EnumerateArray())
        {
            var node = edge.GetProperty("node");
            var status = node.GetProperty("status").GetString() ?? "";
            if (!RailwayDeploys.Counts(status)) continue;
            var meta = node.TryGetProperty("meta", out var mt) && mt.ValueKind == JsonValueKind.Object ? mt : default;
            string message = "";
            if (meta.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "commitMessage", "cliMessage" })
                    if (meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) { message = v.GetString()!; break; }
            return new RailwayDeploy(node.GetProperty("id").GetString() ?? "", status, node.GetProperty("createdAt").GetDateTimeOffset(), message);
        }
        return null;
    }

    public async Task<string> LogsAsync(RailwayDeploy deploy, string name, CancellationToken ct)
    {
        var data = await QueryAsync(
            "query($d:String!){b:buildLogs(deploymentId:$d,limit:1500){message} r:deploymentLogs(deploymentId:$d,limit:400){message}}",
            new { d = deploy.Id }, ct);
        var text = new StringBuilder();
        var when = deploy.CreatedAt.ToLocalTime();
        text.AppendLine($"Railway deploy of {name}: {deploy.Status}. Started {when:MMM d, h:mm tt} (this computer's time).");
        if (deploy.Message.Length > 0) text.AppendLine($"What was deployed: {RailwayDeploys.FirstLine(deploy.Message)}");
        text.AppendLine($"Deployment ID: {deploy.Id}");
        Append("Build logs", data.GetProperty("b"));
        Append("Deploy logs (the app running after the build)", data.GetProperty("r"));
        return text.ToString();

        void Append(string title, JsonElement lines)
        {
            text.AppendLine().AppendLine($"----- {title} -----");
            if (lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0) { text.AppendLine("(none)"); return; }
            foreach (var line in lines.EnumerateArray()) text.AppendLine(RailwayDeploys.Plain(line.GetProperty("message").GetString() ?? ""));
        }
    }
}

internal sealed partial class HostWindow
{
    private static readonly SolidColorBrush DeployLive = Frozen(Color.FromRgb(74, 190, 120));
    private static readonly SolidColorBrush DeployBusy = Frozen(Color.FromRgb(240, 180, 60));
    private static readonly SolidColorBrush DeployFailed = Frozen(Color.FromRgb(235, 80, 80));
    private static readonly SolidColorBrush DeployUnknown = Frozen(Color.FromRgb(110, 110, 116));

    private WpfButton? _deployButton;
    private Ellipse? _deployDot;
    private TextBlock? _deployLabel;
    private readonly System.Windows.Controls.ToolTip _deployTip = new();
    private DispatcherTimer? _deployTimer;
    private DeployWatchConfig? _deployConfig;
    private RailwayDeploy? _deploy;
    private string? _deployError;
    private bool _deployPolling;

    private string DeployName => string.IsNullOrWhiteSpace(_deployConfig?.Name) ? "Service" : _deployConfig!.Name;

    private void AddDeployButton()
    {
        _deployDot = new Ellipse { Width = 10, Height = 10, Fill = DeployUnknown, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        _deployLabel = new TextBlock { FontSize = 10, Foreground = SessionMuted, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        _deployButton = new WpfButton
        {
            Content = new StackPanel { Children = { _deployDot, _deployLabel }, VerticalAlignment = VerticalAlignment.Center },
            Width = 36, Height = 36, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(0), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            Style = FlatButtonStyle, ToolTip = _deployTip, Tag = "deploy", Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_deployButton, "Deploy status");
        _deployButton.Click += async (_, _) => await CopyDeployLogsAsync();
        var menu = CreateFlatMenu();
        var copy = NewMenuItem("Copy logs"); copy.Click += async (_, _) => await CopyDeployLogsAsync(); menu.Items.Add(copy);
        var open = NewMenuItem("Open in Railway"); open.Click += (_, _) => OpenDeployInRailway(); menu.Items.Add(open);
        _deployButton.ContextMenu = menu;
        _bottomButtons.Children.Add(_deployButton);
        if (_options.Fixture) return; // tests never reach Railway
        _deployTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _deployTimer.Tick += async (_, _) => await PollDeployAsync();
        _deployTimer.Start();
        _ = PollDeployAsync();
    }

    private static DeployWatchConfig? ReadDeployConfig(string root)
    {
        try
        {
            var file = System.IO.Path.Combine(root, "deploy-watch.json");
            if (!File.Exists(file)) return null;
            var config = JsonSerializer.Deserialize<DeployWatchConfig>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return config is { IsComplete: true } ? config : null;
        }
        catch { return null; }
    }

    private async Task PollDeployAsync()
    {
        if (_deployPolling) return;
        _deployPolling = true;
        try
        {
            var config = await Task.Run(() => ReadDeployConfig(_root));
            if (config is null) { _deployConfig = null; _deploy = null; RenderDeploy(); return; }
            if (_deployConfig?.Token != config.Token || _deployConfig.ServiceId != config.ServiceId) _deploy = null;
            _deployConfig = config;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _deploy = await new RailwayClient(config).NewestAsync(cts.Token);
            _deployError = null;
        }
        catch (Exception ex) { _deployError = ex is TaskCanceledException ? "Railway didn't answer." : ex.Message; }
        finally { _deployPolling = false; }
        RenderDeploy();
    }

    private void RenderDeploy()
    {
        if (_deployButton is null || _deployDot is null || _deployLabel is null) return;
        _deployButton.Visibility = _deployConfig is null ? Visibility.Collapsed : Visibility.Visible;
        if (_deployConfig is null) return;
        var name = DeployName;
        if (_deploy is not { } d)
        {
            SetDot(DeployUnknown, false);
            _deployLabel.Text = "";
            _deployTip.Content = _deployError is null ? $"{name}: no deploys yet" : $"{name}: can't check Railway\n{_deployError}";
            return;
        }
        var age = DateTimeOffset.UtcNow - d.CreatedAt;
        var active = RailwayDeploys.IsActive(d.Status);
        var failed = RailwayDeploys.IsFailed(d.Status);
        SetDot(active ? DeployBusy : failed ? DeployFailed : d.Status == "SUCCESS" ? DeployLive : DeployUnknown, active);
        _deployLabel.Text = RailwayDeploys.Ago(age);
        _deployLabel.Foreground = active ? DeployBusy : failed ? DeployFailed : SessionMuted;
        var head = active ? $"{name}: {RailwayDeploys.Word(d.Status)} for {RailwayDeploys.Spoken(age)}"
            : failed ? $"{name}: deploy {RailwayDeploys.Word(d.Status)} {RailwayDeploys.Spoken(age)} ago"
            : $"{name}: {RailwayDeploys.Word(d.Status)}, deployed {RailwayDeploys.Spoken(age)} ago";
        var lines = new List<string> { head };
        if (d.Message.Length > 0) lines.Add(RailwayDeploys.FirstLine(d.Message));
        if (_deployError is not null) lines.Add($"Last check failed: {_deployError}");
        lines.Add("Click to copy the logs");
        _deployTip.Content = string.Join("\n", lines);
        // Check faster while something is moving, so the light turns green within seconds of landing.
        if (_deployTimer is not null) _deployTimer.Interval = TimeSpan.FromSeconds(active ? 10 : 20);
    }

    private void SetDot(SolidColorBrush color, bool pulse)
    {
        if (_deployDot is null) return;
        _deployDot.Fill = color;
        var pulsing = _deployDot.Tag is true;
        if (pulse == pulsing) return;
        _deployDot.Tag = pulse;
        _deployDot.BeginAnimation(OpacityProperty, pulse
            ? new DoubleAnimation(1, .3, TimeSpan.FromSeconds(.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private async Task CopyDeployLogsAsync()
    {
        if (_deployConfig is not { } config || _deploy is not { } d) { ShowDeployNote(_deployError ?? "Nothing to copy yet"); return; }
        ShowDeployNote("Copying logs…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var text = await new RailwayClient(config).LogsAsync(d, DeployName, cts.Token);
            System.Windows.Clipboard.SetDataObject(text, true);
            ShowDeployNote("Logs copied. Paste them into Claude Code.");
        }
        catch (Exception ex) { ShowDeployNote("Couldn't copy the logs: " + (ex is TaskCanceledException ? "Railway didn't answer." : ex.Message)); }
    }

    private async void ShowDeployNote(string note)
    {
        var shown = new System.Windows.Controls.ToolTip { Content = note, PlacementTarget = _deployButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Right };
        _deployTip.IsOpen = false;
        if (_deployButton is not null) _deployButton.ToolTip = shown;
        shown.IsOpen = true;
        await Task.Delay(note.EndsWith('…') ? 8000 : 2500);
        if (_deployButton?.ToolTip != shown) return;
        shown.IsOpen = false;
        _deployButton.ToolTip = _deployTip;
    }

    private void OpenDeployInRailway()
    {
        if (_deployConfig is not { } c) return;
        var url = $"https://railway.com/project/{Uri.EscapeDataString(c.ProjectId)}/service/{Uri.EscapeDataString(c.ServiceId)}?environmentId={Uri.EscapeDataString(c.EnvironmentId)}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void StopDeployWatch() => _deployTimer?.Stop();

#if TEST_HARNESS
    // Made-up deploys, so the UI test can picture each state of the light without Railway.
    internal object CaptureDeployStates(string folder)
    {
        var saved = (_deployConfig, _deploy, _deployError);
        _deployConfig = new DeployWatchConfig { Name = "Garage", Token = "x", ProjectId = "x", EnvironmentId = "x", ServiceId = "x" };
        var now = DateTimeOffset.UtcNow;
        var states = new (string File, RailwayDeploy Deploy)[]
        {
            ("deploy-building.png", new("a", "BUILDING", now.AddMinutes(-4), "Merge pull request #169 from Bayfillers/ship/proto/three-plan-signup")),
            ("deploy-live.png", new("b", "SUCCESS", now.AddHours(-2), "Merge pull request #169")),
            ("deploy-failed.png", new("c", "FAILED", now.AddMinutes(-12), "fix: daily boosts")),
        };
        var results = new List<object?>();
        foreach (var (file, deploy) in states)
        {
            _deploy = deploy; _deployError = null; RenderDeploy();
            _deployDot?.BeginAnimation(OpacityProperty, null); if (_deployDot is not null) _deployDot.Tag = false; // a still frame
            results.Add(new { state = deploy.Status, tip = _deployTip.Content, capture = CaptureDock(System.IO.Path.Combine(folder, file)) });
        }
        (_deployConfig, _deploy, _deployError) = saved;
        RenderDeploy();
        return results;
    }
#endif
}
