using System.Net;
using System.Reflection;
using System.Text;

namespace PlcTagViewer.Launcher;

/// <summary>
/// Миниатюрный статический HTTP-сервер на HttpListener.
/// Раздаёт файлы приложения из встроенных ресурсов сборки (web.*).
/// </summary>
public sealed class StaticServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Assembly _asm = Assembly.GetExecutingAssembly();
    private CancellationTokenSource _cts;

    public int Port { get; }
    public string RootUrl => $"http://127.0.0.1:{Port}/";

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".dat"] = "application/octet-stream",
        [".dll"] = "application/octet-stream",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".txt"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml",
        [".map"] = "application/json",
    };

    public StaticServer(int preferredPort = 0)
    {
        // Индекс ресурсов: "web._framework/dotnet.js" -> "/_framework/dotnet.js"
        foreach (var name in _asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("web.", StringComparison.Ordinal)) continue;
            var path = name.Substring(4).Replace('\\', '/');
            _files["/" + path] = name;
        }

        // Порт: предпочтительный (стабильный origin → WebView2 запоминает разрешения),
        // если занят — любой свободный
        int port = 0;
        if (preferredPort > 0)
        {
            try
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, preferredPort);
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            catch { /* занят — берём случайный */ }
        }
        if (port == 0)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
            catch { break; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        try
        {
            var path = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath);
            if (path == "/" || path.Length == 0) path = "/index.html";
            // защита от выхода за корень
            path = path.Replace("..", "");

            if (_files.TryGetValue(path, out var resName))
            {
                using var s = _asm.GetManifestResourceStream(resName);
                var ext = Path.GetExtension(path);
                ctx.Response.ContentType = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.ContentLength64 = s.Length;
                s.CopyTo(ctx.Response.OutputStream);
            }
            else
            {
                // SPA-fallback: неизвестный путь -> index.html
                if (_files.TryGetValue("/index.html", out var idx))
                {
                    using var s = _asm.GetManifestResourceStream(idx);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = s.Length;
                    s.CopyTo(ctx.Response.OutputStream);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    var buf = Encoding.UTF8.GetBytes("404");
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                }
            }
        }
        catch { }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
