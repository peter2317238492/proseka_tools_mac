using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace ProsekaTools.Avalonia.Services;

public sealed class CaptureServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly Func<string> _localIpProvider;
    private readonly Action<string> _log;
    private WebApplication? _app;

    public CaptureServer(int port, Func<string> localIpProvider, Action<string> log)
    {
        _port = port;
        _localIpProvider = localIpProvider;
        _log = log;
    }

    public bool IsRunning => _app is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, _port);
        });

        var app = builder.Build();

        app.MapGet("/", () =>
        {
            var html = BuildIndexHtml(_localIpProvider(), _port);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/status", () => Results.Json(new
        {
            status = "ok",
            ip = _localIpProvider(),
            port = _port,
            time = DateTimeOffset.Now
        }));

        app.MapGet("/upload.js", () =>
        {
            var js = BuildUploadJs(_localIpProvider(), _port);
            return Results.Text(js, "application/javascript; charset=utf-8");
        });

        app.MapPost("/upload", async (HttpContext ctx) =>
        {
            var originalUrl = ctx.Request.Headers["X-Original-Url"].ToString();
            var apiType = ExtractApiType(originalUrl);
            var filename = GenerateFilename(apiType, originalUrl);

            byte[] body;
            await using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms, cancellationToken);
                body = ms.ToArray();
            }

            var savePath = BuildSavePath(apiType, filename);
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            await File.WriteAllBytesAsync(savePath, body, cancellationToken);

            _log($"[{apiType}] saved {Path.GetFileName(savePath)} ({body.Length / 1024.0:F2} KB)");

            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            return Results.Text("ok", "text/plain; charset=utf-8");
        });

        app.MapFallback(() => Results.Text("Not Found", "text/plain; charset=utf-8", statusCode: 404));

        await app.StartAsync(cancellationToken);
        _app = app;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null) return;
        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static string ExtractApiType(string url)
    {
        if (string.IsNullOrEmpty(url)) return "unknown";
        if (Regex.IsMatch(url, @"/mysekai(\?|$)", RegexOptions.IgnoreCase)) return "mysekai";
        if (Regex.IsMatch(url, @"/suite/", RegexOptions.IgnoreCase)) return "suite";
        return "unknown";
    }

    private static string GenerateFilename(string apiType, string originalUrl)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var uidMatch = Regex.Match(originalUrl ?? string.Empty, @"/user/(\d+)", RegexOptions.IgnoreCase);
        var userStr = uidMatch.Success ? $"_user{uidMatch.Groups[1].Value}" : string.Empty;
        var pid = Environment.ProcessId;
        return $"{apiType}{userStr}_{ts}_{pid}.bin";
    }

    private static string BuildSavePath(string apiType, string filename)
    {
        var category = (apiType == "mysekai" || apiType == "suite") ? apiType : "unknown";
        var folder = Path.Combine(AppPaths.GetCapturesRoot(), category);
        return Path.Combine(folder, filename);
    }

    private static string BuildIndexHtml(string localIp, int port)
    {
        var html = """
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Proseka Tools Capture Test</title>
  <style>
    body { font-family: -apple-system, Segoe UI, Roboto, Helvetica, Arial, sans-serif; margin: 16px; }
    .ok { color: #0a7a0a; }
    .fail { color: #b00020; }
    .card { border: 1px solid #ddd; border-radius: 8px; padding: 12px; margin: 12px 0; box-shadow: 0 1px 2px rgba(0,0,0,.04); }
    button { padding: 8px 14px; border-radius: 6px; border: 1px solid #ccc; background: #f7f7f7; }
    code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  </style>
</head>
<body>
  <h2>Capture Server Reachability</h2>
  <div class="card">
    <div>Server: <b>http://__IP__:__PORT__/</b></div>
    <div id="ua">Client: <code></code></div>
    <div id="status">Status: <span>unknown</span></div>
    <div style="margin-top:8px">
      <button id="btn">Ping /status</button>
    </div>
  </div>

  <div class="card">
    <div>Upload helper: <a href="/upload.js">/upload.js</a></div>
  </div>

  <script>
    const $ = s => document.querySelector(s);
    $('#ua').querySelector('code').textContent = navigator.userAgent;
    async function ping(){
      const el = $('#status').querySelector('span');
      el.textContent = 'checking...';
      try {
        const r = await fetch('/status', { cache: 'no-store' });
        if(!r.ok) throw new Error('HTTP ' + r.status);
        const j = await r.json();
        el.textContent = 'OK @ ' + j.time;
        el.className = 'ok';
      } catch(e){
        el.textContent = 'FAILED: ' + e.message;
        el.className = 'fail';
      }
    }
    $('#btn').addEventListener('click', ping);
    ping();
  </script>
</body>
</html>
""";

        return html.Replace("__IP__", localIp).Replace("__PORT__", port.ToString());
    }

    private static string BuildUploadJs(string localIp, int port)
    {
        var js = $@"
const upload = () => {{
  $httpClient.post({{
    url: 'http://{localIp}:{port}/upload',
    headers: {{
      'X-Original-Url': $request.url,
      'X-Request-Path': $request.path
    }},
    body: $response.body
  }}, (error) => $done({{}}));
}};
upload();
";

        return js.Trim();
    }
}
