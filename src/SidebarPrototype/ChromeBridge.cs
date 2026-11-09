using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SidebarPrototype;

// The Sessions extension in Chrome talks to the sidebar through Chrome native messaging. Chrome starts this same
// exe with the extension's address as the first argument. That copy opens no windows: it only passes messages
// between Chrome (its standard input and output) and the running sidebar (a named pipe only this Windows user
// can open). Every message is a 4-byte length followed by JSON, the format Chrome uses.
public static class ChromeBridgeRelay
{
    public const string HostName = "com.theusuals.bridge";
    internal static string PipeName => "TheUsuals.ChromeBridge." + Environment.UserName;

    public static bool IsChromeLaunch(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    public static int Run()
    {
        var fromChrome = Console.OpenStandardInput();
        var toChrome = Console.OpenStandardOutput();
        NamedPipeClientStream? sidebar = null;
        var gate = new object();

        // Chrome closing our input means Chrome is done with us.
        _ = Task.Run(() =>
        {
            while (Frames.Read(fromChrome) is { } frame)
                lock (gate)
                    try { if (sidebar is { IsConnected: true }) Frames.Write(sidebar, frame); } catch { }
            Environment.Exit(0);
        });

        // Wait for the sidebar to be running, pass its messages to Chrome, and reconnect if it restarts.
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { pipe.Connect(1000); }
            catch { pipe.Dispose(); Thread.Sleep(2000); continue; }
            lock (gate) sidebar = pipe;
            try { while (Frames.Read(pipe) is { } frame) Frames.Write(toChrome, frame); } catch { }
            lock (gate) sidebar = null;
            pipe.Dispose();
            try { Frames.Write(toChrome, "{\"type\":\"bye\"}"u8.ToArray()); } catch { }
        }
    }
}

internal static class Frames
{
    private const int MaxFrame = 32 * 1024 * 1024;

    public static byte[]? Read(Stream s)
    {
        var head = new byte[4];
        if (!Fill(s, head)) return null;
        var length = BitConverter.ToInt32(head);
        if (length < 0 || length > MaxFrame) return null;
        var body = new byte[length];
        return Fill(s, body) ? body : null;
    }

    public static void Write(Stream s, byte[] body)
    {
        s.Write(BitConverter.GetBytes(body.Length));
        s.Write(body);
        s.Flush();
    }

    private static bool Fill(Stream s, byte[] buffer)
    {
        for (var at = 0; at < buffer.Length;)
        {
            var n = s.Read(buffer, at, buffer.Length - at);
            if (n <= 0) return false;
            at += n;
        }
        return true;
    }
}

// The sidebar's end: waits for the relay, hands each message from Chrome to the window, and sends messages back.
internal sealed class ChromeBridge : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _writeGate = new();
    private NamedPipeServerStream? _pipe;

    public event Action<JsonElement>? MessageReceived;
    public event Action? ConnectionChanged;
    public bool IsConnected => _pipe is { IsConnected: true };

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    ChromeBridgeRelay.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                _pipe = pipe;
                ConnectionChanged?.Invoke();
                while (await Task.Run(() => Frames.Read(pipe)) is { } frame)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(frame);
                        MessageReceived?.Invoke(doc.RootElement.Clone());
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(1000); }
            finally
            {
                if (_pipe is not null) { _pipe = null; ConnectionChanged?.Invoke(); }
            }
        }
    }

    public void Send(object message)
    {
        var pipe = _pipe;
        if (pipe is not { IsConnected: true }) return;
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        try { lock (_writeGate) Frames.Write(pipe, body); } catch { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _pipe?.Dispose(); } catch { }
    }

    // Tell Chrome (and Edge) where the relay is and which extensions may use it. Runs at every start, so moving
    // the app folder or installing the extension later fixes itself.
    public static void Register(string dataRoot)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var ids = FindSessionsExtensions().Concat(StoreExtensionIds).Distinct().ToArray();
        var manifest = Path.Combine(dataRoot, "chrome-bridge.json");
        File.WriteAllText(manifest, new JsonObject
        {
            ["name"] = ChromeBridgeRelay.HostName,
            ["description"] = "Connects the Sessions extension to the sidebar",
            ["path"] = exe,
            ["type"] = "stdio",
            ["allowed_origins"] = new JsonArray(ids.Select(id => (JsonNode)$"chrome-extension://{id}/").ToArray()),
        }.ToJsonString());
        foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge" })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\{browser}\NativeMessagingHosts\{ChromeBridgeRelay.HostName}");
            key.SetValue("", manifest);
        }
    }

    // Filled in once the extension is in the Chrome Web Store, where its ID never changes.
    private static readonly string[] StoreExtensionIds = [];

    // A copy of the extension loaded from a folder gets an ID made from its folder path, so look it up in each
    // browser profile: any installed extension called Sessions that asks to talk to native apps.
    private static IEnumerable<string> FindSessionsExtensions()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var userData in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data" })
        {
            var root = Path.Combine(local, userData);
            if (!Directory.Exists(root)) continue;
            foreach (var profile in Directory.EnumerateDirectories(root))
            foreach (var prefs in new[] { "Secure Preferences", "Preferences" })
            {
                var file = Path.Combine(profile, prefs);
                if (!File.Exists(file)) continue;
                List<string> found = [];
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(stream);
                    if (!doc.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var settings)) continue;
                    foreach (var entry in settings.EnumerateObject())
                    {
                        if (!entry.Value.TryGetProperty("path", out var p) || p.GetString() is not { Length: > 0 } path) continue;
                        var dir = Path.IsPathRooted(path) ? path : Path.Combine(profile, "Extensions", path);
                        if (IsSessionsExtension(Path.Combine(dir, "manifest.json"))) found.Add(entry.Name);
                    }
                }
                catch { }
                foreach (var id in found) yield return id;
            }
        }
    }

    private static bool IsSessionsExtension(string manifestFile)
    {
        try
        {
            if (!File.Exists(manifestFile)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestFile));
            var m = doc.RootElement;
            return m.TryGetProperty("name", out var name) && name.GetString() == "Sessions" &&
                m.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array &&
                perms.EnumerateArray().Any(x => x.GetString() == "nativeMessaging");
        }
        catch { return false; }
    }
}
