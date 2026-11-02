using System.Text;
using System.Text.Json;
using Macast.Core.Settings;
using Macast.Core.Upnp;
using Macast.Core.Utils;

namespace Macast.Core.Server;

public sealed class MacastHttpServer : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly DlnaProtocol _protocol;
    private readonly Func<string> _description;
    private readonly SocketHttpServer _server;

    public int Port => _server.Port;

    public MacastHttpServer(AppSettings settings, DlnaProtocol protocol, Func<string> description, bool loopbackOnly = false)
    {
        _settings = settings;
        _protocol = protocol;
        _description = description;
        _server = new SocketHttpServer(HandleAsync, loopbackOnly);
    }

    public Task StartAsync(int preferredPort, CancellationToken cancellationToken = default) =>
        _server.StartAsync(preferredPort, cancellationToken);

    private Task<HttpResponseData?> HandleAsync(HttpRequestData request, Stream stream, CancellationToken token)
    {
        var response = Handle(request);
        return Task.FromResult<HttpResponseData?>(response);
    }

    private HttpResponseData Handle(HttpRequestData request)
    {
        if (!request.Protocol.StartsWith("HTTP/", StringComparison.Ordinal))
            return Status(400, "Bad Request");
        var path = Uri.UnescapeDataString(request.Path.Split('?', 2)[0]);
        if (request.Method is "GET" or "HEAD")
        {
            if (path.Equals("/description.xml", StringComparison.OrdinalIgnoreCase))
                return Content(Encoding.UTF8.GetBytes(_description()), "text/xml; charset=utf-8");
            if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                return Content(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    status = "ok", port = Port, friendlyName = _settings.FriendlyName,
                    usn = _settings.GetUsn(), version = _settings.Version,
                    renderer = _protocol.Renderer?.Name, mpv = _protocol.Renderer?.Available ?? false
                }), "application/json; charset=utf-8");
            if (path.StartsWith("/dlna/", StringComparison.OrdinalIgnoreCase))
            {
                var name = path[6..];
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.Contains(':'))
                    return Status(404, "Not Found");
                var file = Path.Combine(AssetPaths.XmlDir, name);
                var extension = Path.GetExtension(name).ToLowerInvariant();
                if (!File.Exists(file) || extension is not (".xml" or ".csv"))
                    return Status(404, "Not Found");
                return Content(File.ReadAllBytes(file), extension == ".xml" ? "text/xml; charset=utf-8" : "text/csv; charset=utf-8");
            }
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return Status(404, "Not Found");
        var service = parts[0];
        if (request.Method == "POST" && parts[1].Equals("action", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var response = Content(_protocol.Call(request.Body), "text/xml; charset=utf-8");
                response.Headers["EXT"] = string.Empty;
                return response;
            }
            catch (Exception ex)
            {
                Log.Warn($"SOAP action failed: {ex.Message}");
                return new HttpResponseData
                {
                    Status = 500, StatusText = "Internal Server Error",
                    Body = DlnaProtocol.BuildSoapFault(402, ex.Message)
                };
            }
        }
        if (!parts[1].Equals("event", StringComparison.OrdinalIgnoreCase)) return Status(404, "Not Found");
        var sid = request.Header("SID");
        if (request.Method == "UNSUBSCRIBE")
            return !string.IsNullOrEmpty(sid) && _protocol.Unsubscribe(sid)
                ? new HttpResponseData() : Status(412, "Precondition Failed");
        if (request.Method != "SUBSCRIBE") return Status(404, "Not Found");
        try
        {
            var timeout = ParseTimeout(request.Header("TIMEOUT"));
            var response = new HttpResponseData();
            if (sid.Length > 0)
            {
                if (!_protocol.Renew(sid, timeout)) return Status(412, "Precondition Failed");
                response.Headers["SID"] = sid;
                response.Headers["TIMEOUT"] = $"Second-{timeout}";
            }
            else
            {
                var subscription = _protocol.Subscribe(service, request.Header("CALLBACK"), timeout, request.RemoteAddress);
                response.Headers["SID"] = subscription.Sid;
                response.Headers["TIMEOUT"] = subscription.Timeout;
            }
            return response;
        }
        catch (Exception ex)
        {
            Log.Warn($"SUBSCRIBE failed: {ex.Message}");
            return Status(412, "Precondition Failed");
        }
    }

    private static int ParseTimeout(string raw) => raw.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(raw[7..], out var seconds) && seconds > 0 ? Math.Min(seconds, 86400) : 1800;

    private static HttpResponseData Content(byte[] bytes, string contentType)
    {
        var response = new HttpResponseData { Body = bytes };
        response.Headers["Content-Type"] = contentType;
        return response;
    }

    private static HttpResponseData Status(int status, string text) => new() { Status = status, StatusText = text };
    public Task StopAsync() => _server.StopAsync();
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
