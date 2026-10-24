using System.Net;
using System.Text;
using System.Text.Json;

namespace SidebarPrototype;

internal sealed partial class HostWindow
{
    // Base address of the local fixture server; null outside fixture runs.
    private string? _fixtureBase;
    private HttpListener? _fixtureServer;
    private int _fixtureCallbackCount;
    private int _fixtureAppReturns;

    private void StartFixture()
    {
        var port = new Random().Next(18000, 49000);
        _fixtureBase = $"http://127.0.0.1:{port}/";
        _fixtureServer = new HttpListener();
        _fixtureServer.Prefixes.Add(_fixtureBase);
        _fixtureServer.Prefixes.Add($"http://localhost:{port}/");
        _fixtureServer.Start();
        _ = Task.Run(async () =>
        {
            while (_fixtureServer.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _fixtureServer.GetContextAsync();
                }
                catch
                {
                    return;
                }
                try
                {
                    if (ctx.Request.Url!.AbsolutePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        var wave = RuntimeProbe.CreateAudioWave();
                        ctx.Response.ContentType = "audio/wav";
                        ctx.Response.ContentLength64 = wave.Length;
                        await ctx.Response.OutputStream.WriteAsync(wave);
                        ctx.Response.Close();
                        continue;
                    }
                    if (ctx.Request.Url.AbsolutePath == "/app-return")
                        Interlocked.Increment(ref _fixtureAppReturns);
                    if (ctx.Request.Url.AbsolutePath == "/google-auth-intent")
                    {
                        ctx.Response.StatusCode = 302;
                        ctx.Response.RedirectLocation = _fixtureBase + "app-return";
                        ctx.Response.Close();
                        continue;
                    }
                    if (ctx.Request.Url.AbsolutePath == "/auth-post")
                    {
                        ctx.Response.StatusCode = 307;
                        ctx.Response.RedirectLocation = _fixtureBase + "auth-callback";
                        ctx.Response.Close();
                        continue;
                    }
                    if (ctx.Request.Url.AbsolutePath == "/main-redirect")
                    {
                        ctx.Response.StatusCode = 302;
                        ctx.Response.RedirectLocation = $"http://localhost:{port}/main-final";
                        ctx.Response.Close();
                        continue;
                    }
                    if (ctx.Request.Url.AbsolutePath == "/auth-callback")
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream);
                        var body = await reader.ReadToEndAsync();
                        if (
                            ctx.Request.HttpMethod != "POST"
                            || body != "fixture_nonce=one-use"
                            || Interlocked.Increment(ref _fixtureCallbackCount) != 1
                        )
                        {
                            ctx.Response.StatusCode = 400;
                            ctx.Response.Close();
                            continue;
                        }
                    }
                    var authScript =
                        ctx.Request.Url.AbsolutePath == "/auth-start"
                            ? $"const form=document.createElement('form');form.method='POST';form.action='http://localhost:{port}/auth-post';const n=document.createElement('input');n.name='fixture_nonce';n.value='one-use';form.append(n);document.body.append(form);form.submit();"
                        : ctx.Request.Url.AbsolutePath == "/auth-callback"
                            ? "if(window.opener){window.opener.postMessage('fixture-auth-complete','*');setTimeout(()=>window.close(),250)}"
                        : "";
                    var popupScript =
                        ctx.Request.Url.AbsolutePath == "/popup"
                            ? "if(window.opener){window.opener.postMessage('fixture-popup-ok','*');window.opener.postMessage({closeBridge:window.close.toString().includes('sidebar-auth-surface-close')},'*');setTimeout(()=>window.close(),250)}"
                            : "";
                    var html =
                        """<!doctype html><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1"><title>Fixture</title><style>body{font:18px Arial;background:#eee;padding:20px;height:2600px}@media(prefers-color-scheme:dark){body{background:#171717;color:#eee}}button{padding:10px}</style><h1>Sidebar compatibility fixture</h1><input id=input value='input-retention'><input type=file id=upload><button onclick="document.querySelector('#test-dialog').showModal()">Test dialog</button><dialog id=test-dialog><p>Fixture dialog</p><button onclick="document.querySelector('#test-dialog').close()">Dismiss</button></dialog><p>Profile storage marker: <b id=marker></b></p><p>Page instance: <b id=instance></b></p><p>Scroll, hide, switch and return. The instance id should remain stable.</p><button onclick="localStorage.marker='saved-'+location.pathname;document.querySelector('#marker').textContent=localStorage.marker">Write profile marker</button><button onclick="window.open('/popup','login-popup')">Open owned popup</button><audio id=audio controls loop src='/tone.wav'></audio><div style='height:1800px'></div><script>window.instanceId=crypto.randomUUID();document.querySelector('#instance').textContent=window.instanceId;document.querySelector('#marker').textContent=localStorage.marker||'(empty)';window.fixturePopup='';window.fixtureCloseBridge=false;window.addEventListener('message',e=>{if(typeof e.data==='string')window.fixturePopup=e.data;if(e.data&&typeof e.data.closeBridge==='boolean')window.fixtureCloseBridge=e.data.closeBridge});"""
                        + popupScript
                        + authScript
                        + "window.requestAgent="
                        + JsonSerializer.Serialize(ctx.Request.UserAgent)
                        + ";window.requestMobile="
                        + JsonSerializer.Serialize(ctx.Request.Headers["Sec-CH-UA-Mobile"])
                        + ";</script>";
                    var bytes = Encoding.UTF8.GetBytes(html);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch
                {
                    try
                    {
                        ctx.Response.Abort();
                    }
                    catch { }
                }
            }
        });
    }
}
