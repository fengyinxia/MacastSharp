using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Macast.Core.Settings;
using Macast.Core.Upnp;
using Macast.Core.Utils;

namespace Macast.Core.Server;

public sealed class MacastHttpServer : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly DlnaProtocol _protocol;
    private readonly Func<string> _description;
    private WebApplication? _app;

    public int Port { get; private set; }

    public MacastHttpServer(AppSettings settings, DlnaProtocol protocol, Func<string> description)
    {
        _settings = settings;
        _protocol = protocol;
        _description = description;
    }

    public async Task StartAsync(int preferredPort, CancellationToken cancellationToken = default)
    {
        var candidates = preferredPort > 0 ? new[] { preferredPort, 0 } : new[] { 0 };
        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            try
            {
                await StartOnPortAsync(candidate, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Warn($"HTTP listen on port {candidate} failed: {ex.Message}");
                await StopInternalAsync();
            }
        }

        throw new InvalidOperationException("HTTP server could not bind to any port", lastError);
    }

    private async Task StartOnPortAsync(int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.ListenAnyIP(port);
        });

        var app = builder.Build();
        _app = app;

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(AssetPaths.XmlDir),
            RequestPath = "/dlna",
            ContentTypeProvider = BuildContentTypeProvider()
        });

        MapRoutes(app);

        await app.StartAsync(cancellationToken);

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault() ?? app.Urls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"cannot determine HTTP address from '{address}'");
        }

        Port = uri.Port;
        Log.Info($"HTTP server listening on 0.0.0.0:{Port}");
    }

    private static IContentTypeProvider BuildContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".xml"] = "text/xml; charset=utf-8";
        provider.Mappings[".csv"] = "text/csv; charset=utf-8";
        return provider;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/description.xml", () =>
        {
            var bytes = Encoding.UTF8.GetBytes(_description());
            return Results.Bytes(bytes, "text/xml; charset=\"utf-8\"");
        });

        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            port = Port,
            friendlyName = _settings.FriendlyName,
            usn = _settings.GetUsn(),
            version = _settings.Version,
            renderer = _protocol.Renderer?.Name,
            mpv = _protocol.Renderer?.Available ?? false
        }));

        app.MapPost("/{service}/action", async (HttpContext context) =>
        {
            try
            {
                using var memory = new MemoryStream();
                await context.Request.Body.CopyToAsync(memory, context.RequestAborted);
                var body = memory.ToArray();
                var response = _protocol.Call(body);

                context.Response.ContentType = "text/xml; charset=\"utf-8\"";
                context.Response.Headers["EXT"] = string.Empty;
                await context.Response.Body.WriteAsync(response, context.RequestAborted);
            }
            catch (Exception ex)
            {
                Log.Warn($"SOAP action failed: {ex.Message}");
                var fault = DlnaProtocol.BuildSoapFault(402, ex.Message);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/xml; charset=\"utf-8\"";
                await context.Response.Body.WriteAsync(fault, context.RequestAborted);
            }
        });

        app.MapMethods("/{service}/event", new[] { "SUBSCRIBE" }, async (HttpContext context) =>
        {
            var service = context.Request.RouteValues["service"]?.ToString() ?? string.Empty;
            var sid = context.Request.Headers["SID"].ToString();
            var callback = context.Request.Headers["CALLBACK"].ToString();
            var timeout = ParseTimeout(context.Request.Headers["TIMEOUT"].ToString());

            try
            {
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    if (!_protocol.Renew(sid, timeout))
                    {
                        context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                        return;
                    }

                    context.Response.Headers["SID"] = sid;
                    context.Response.Headers["TIMEOUT"] = $"Second-{timeout}";
                }
                else if (!string.IsNullOrWhiteSpace(callback))
                {
                    var subscription = _protocol.Subscribe(service, callback, timeout);
                    context.Response.Headers["SID"] = subscription.Sid;
                    context.Response.Headers["TIMEOUT"] = subscription.Timeout;
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
            }
            catch (Exception ex)
            {
                Log.Warn($"SUBSCRIBE failed: {ex.Message}");
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            }
        });

        app.MapMethods("/{service}/event", new[] { "UNSUBSCRIBE" }, (HttpContext context) =>
        {
            var sid = context.Request.Headers["SID"].ToString();
            if (string.IsNullOrWhiteSpace(sid) || !_protocol.Unsubscribe(sid))
            {
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                return Task.CompletedTask;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
    }

    private static int ParseTimeout(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 1800;
        }

        var value = raw.Trim();
        if (value.StartsWith("Second-", StringComparison.OrdinalIgnoreCase))
        {
            var tail = value[7..];
            if (int.TryParse(tail, out var seconds) && seconds > 0)
            {
                return Math.Min(seconds, 86400);
            }
        }

        return 1800;
    }

    private async Task StopInternalAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // ignore
        }

        try
        {
            await _app.DisposeAsync();
        }
        catch
        {
            // ignore
        }

        _app = null;
    }

    public async Task StopAsync()
    {
        await StopInternalAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync();
    }
}