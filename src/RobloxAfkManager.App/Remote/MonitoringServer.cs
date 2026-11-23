using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobloxAfkManager.Core;

namespace RobloxAfkManager.App.Remote;

public sealed class MonitoringServer(MonitoringFeed feed, IAppLog log) : IAsyncDisposable
{
    private WebApplication? app;
    private CancellationTokenSource? cancellation;
    private Task? captureTask;
    public const int DefaultPort = 8765;
    public string AccessCode { get; private set; } = "";
    public bool Running => app is not null;
    public string Address { get; private set; } = "";

    public async Task StartAsync(int port = DefaultPort)
    {
        if (app is not null) return;
        AccessCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + AccessCode));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        // Do not load user launch URLs, log authorization headers or create a second console lifetime.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxRequestBodySize = 2048;
            options.Limits.MaxRequestHeadersTotalSize = 16384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        var server = builder.Build();
        var assets = new Dictionary<string, (byte[] Bytes, string Type)>
        {
            ["/"] = (ReadAsset("index.html"), "text/html; charset=utf-8"),
            ["/dashboard.css"] = (ReadAsset("dashboard.css"), "text/css; charset=utf-8"),
            ["/dashboard.js"] = (ReadAsset("dashboard.js"), "text/javascript; charset=utf-8"),
            ["/app-icon.png"] = (ReadAsset("app-icon.png"), "image/png"),
            ["/apple-touch-icon.png"] = (ReadAsset("apple-touch-icon.png"), "image/png")
        };
        server.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            string host = context.Request.Host.Host;
            if (!(host is "127.0.0.1" or "localhost" || host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)))
            { context.Response.StatusCode = 400; return; }
            if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) || !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            { context.Response.StatusCode = 403; return; }
            if (context.Request.Method != "GET") { context.Response.StatusCode = 405; context.Response.Headers.Allow = "GET"; return; }
            string path = context.Request.Path.Value ?? "/";
            if (assets.TryGetValue(path, out var asset))
            {
                context.Response.ContentType = asset.Type;
                await context.Response.Body.WriteAsync(asset.Bytes, context.RequestAborted); return;
            }
            if (!path.StartsWith("/api/", StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
            byte[] provided = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expected, provided)) { context.Response.StatusCode = 401; return; }
            if (path == "/api/clients") { await context.Response.WriteAsJsonAsync(feed.Read(), context.RequestAborted); return; }
            if (path.StartsWith("/api/preview/", StringComparison.Ordinal))
            {
                var jpeg = feed.Image(path[13..]);
                if (jpeg is null) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "image/jpeg";
                await context.Response.Body.WriteAsync(jpeg, context.RequestAborted); return;
            }
            context.Response.StatusCode = 404;
        });
        try
        {
            feed.ResetViewers();
            await server.StartAsync().ConfigureAwait(false);
            app = server;
            Address = server.Urls.Single();
            cancellation = new();
            captureTask = Task.Run(() => feed.RunAsync(cancellation.Token));
            log.Write("Remote monitoring started on loopback. Tailscale setup is managed separately.");
        }
        catch { AccessCode = ""; await server.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        var server = app;
        if (server is null) return;
        app = null;
        cancellation!.Cancel();
        try
        {
            await server.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (captureTask is not null) await captureTask.ConfigureAwait(false);
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
            cancellation.Dispose(); cancellation = null; captureTask = null;
            feed.ResetViewers(); AccessCode = ""; Address = "";
            log.Write("Remote monitoring stopped; capture resources released.");
        }
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RobloxAfkManager.App.Remote.Web." + name)
            ?? throw new InvalidOperationException("Missing dashboard asset: " + name);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
}
