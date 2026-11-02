using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Macast.Core.Plugins;
using Macast.Core.Renderer;
using Macast.Core.Server;
using Macast.Core.Settings;
using Macast.Core.Upnp;
using Macast.Core.Utils;

namespace Macast.Core.Protocols.Nva;

/// <summary>
/// The NVA (nirvana / 哔哩必连) protocol service, ported from nirvana.py.
///
/// It keeps the stock <see cref="DlnaProtocol"/> for SOAP / state / GENA and puts the raw
/// <see cref="SocketHttpServer"/> in front of it, because the bilibili client speaks "NVA/1.0"
/// request lines and upgrades one connection into a binary control channel.
/// </summary>
public sealed class NvaProtocolService : IProtocolService, INvaCastingHost
{
    private const string NirvanaControl = NvaActions.ServiceName;

    // nirvana.py NVAHandler.build_description: header_extra / service_extra
    private const string HeaderExtra =
        "<X_brandName>Macast</X_brandName>\n" +
        "<hostVersion>25</hostVersion>\n" +
        "<ottVersion>106400</ottVersion>\n" +
        "<channelName>master</channelName>\n" +
        "<capability>254</capability>";

    private const string ServiceExtra =
        "<service>\n" +
        "<serviceType>urn:app-bilibili-com:service:NirvanaControl:3</serviceType>\n" +
        "<serviceId>urn:app-bilibili-com:serviceId:NirvanaControl</serviceId>\n" +
        "<controlURL>NirvanaControl/action</controlURL>\n" +
        "<eventSubURL>NirvanaControl/event</eventSubURL>\n" +
        "<SCPDURL>dlna/NirvanaControl.xml</SCPDURL>\n" +
        "</service>";

    private static readonly string[] NvaHandshakeMethods = { "SETUP", "RESTORE", "STARTRESTORE" };

    private readonly AppSettings _settings;
    private readonly DlnaProtocol _dlna;
    private readonly SocketHttpServer _server;
    private string _description = string.Empty;

    public NvaProtocolService(AppSettings settings, bool loopbackOnly = false, BilibiliApi? bilibili = null)
    {
        _settings = settings;
        _dlna = new DlnaProtocol(settings);
        Bilibili = bilibili ?? new BilibiliApi();
        Casting = new NvaCasting(this);
        Actions = new NvaActions(this);
        _server = new SocketHttpServer(HandleAsync, loopbackOnly);
    }

    public NvaCasting Casting { get; }

    public NvaActions Actions { get; }

    public BilibiliApi Bilibili { get; }

    /// <summary>The embedded DLNA protocol (also serves AVTransport/RenderingControl here).</summary>
    public DlnaProtocol Protocol => _dlna;

    public AppSettings Settings => _settings;

    public string Id => "Auto";

    public string Description => _description;

    public int Port => _server.Port;

    public IRenderer? Renderer
    {
        get => _dlna.Renderer;
        set => _dlna.Renderer = value;
    }

    // ---- lifecycle -----------------------------------------------------

    public async Task StartAsync(int preferredPort, CancellationToken cancellationToken = default)
    {
        _dlna.Start();
        await _server.StartAsync(preferredPort, cancellationToken);
        RefreshDescription();
        Casting.Start();
        Log.Info($"DLNA + NVA automatic service ready on port {Port}");
    }

    public async Task StopAsync()
    {
        await _server.StopAsync();
        await Casting.StopBackgroundAsync();
        Casting.DisconnectAll();
        await _dlna.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Casting.DisposeAsync();
        await _server.DisposeAsync();
        await _dlna.DisposeAsync();
    }

    public void RefreshDescription()
    {
        var description = DeviceDescription.Build(_settings, HeaderExtra, ServiceExtra);
        _description = ApplyBilibiliBranding(description);
        // The Android client gates its TV controls on this exact friendly-name marker.
        var name = _settings.FriendlyName;
        var nvaName = name.Contains("我的小电视", StringComparison.Ordinal)
            ? name : $"我的小电视 · {name}";
        _description = _description.Replace($"<friendlyName>{DeviceDescription.Escape(name)}</friendlyName>",
            $"<friendlyName>{DeviceDescription.Escape(nvaName)}</friendlyName>", StringComparison.Ordinal);
    }

    /// <summary>
    /// nirvana.py build_description replaces the stock manufacturer/model fields with the
    /// bilibili TV ones. TV controls additionally require the friendly-name marker above.
    /// </summary>
    private static string ApplyBilibiliBranding(string description)
    {
        return description
            .Replace("<manufacturer>xfangfang</manufacturer>",
                "<manufacturer>Bilibili Inc.</manufacturer>", StringComparison.Ordinal)
            .Replace("<manufacturerURL>https://github.com/xfangfang</manufacturerURL>",
                "<manufacturerURL>https://bilibili.com/</manufacturerURL>", StringComparison.Ordinal)
            .Replace("<modelDescription>AVTransport Media Renderer</modelDescription>",
                "<modelDescription>云视听小电视</modelDescription>", StringComparison.Ordinal)
            .Replace("<modelURL>https://xfangfang.github.io/Macast</modelURL>",
                "<modelURL>https://app.bilibili.com/</modelURL>", StringComparison.Ordinal);
    }

    // ---- protocol state ------------------------------------------------

    public string GetState(string name) => _dlna.GetState(name);

    public void SetState(string name, string value)
    {
        // Only forward real changes: renderers echo state back (mpv observe events),
        // and every echo used to re-broadcast progress events.
        var previous = _dlna.GetState(name);
        _dlna.SetState(name, value);
        if (!string.Equals(previous, value, StringComparison.Ordinal))
        {
            Casting.OnStateChanged(name, value);
        }
    }

    public void SetState(string name, bool value) => SetState(name, value ? "true" : "false");

    public void SetState(string name, int value) =>
        SetState(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ---- request routing -----------------------------------------------

    private async Task<HttpResponseData?> HandleAsync(HttpRequestData request, Stream stream, CancellationToken cancellationToken)
    {
        var path = NormalizePath(request.Path);

        // nirvana.py detaches the socket for SETUP / RESTORE / STARTRESTORE whatever the
        // request-line protocol is; only the response protocol name follows "NVA/1.0".
        if (IsNvaHandshake(request.Method))
        {
            if (!request.IsNva)
            {
                Log.Debug($"NVA: control channel opened with protocol '{request.Protocol}' instead of NVA/1.0");
            }

            return await HandleControlChannelAsync(request, stream, cancellationToken);
        }

        switch (request.Method)
        {
            case "GET":
            case "HEAD":
                return HandleGet(path);

            case "POST":
                if (TrySplitService(path, "/action", out var actionService))
                {
                    return await HandleActionAsync(actionService, request.Body, cancellationToken);
                }

                break;

            case "SUBSCRIBE":
                if (TrySplitService(path, "/event", out var subscribeService))
                {
                    return HandleEvent(subscribeService, request, unsubscribe: false);
                }

                break;

            case "UNSUBSCRIBE":
                if (TrySplitService(path, "/event", out var unsubscribeService))
                {
                    return HandleEvent(unsubscribeService, request, unsubscribe: true);
                }

                break;
        }

        Log.Debug($"NVA: 404 {request.Method} {request.Path}");
        return NotFound();
    }

    private static bool IsNvaHandshake(string method) =>
        NvaHandshakeMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string raw)
    {
        var query = raw.IndexOf('?');
        var path = query < 0 ? raw : raw[..query];
        if (path.Length == 0)
        {
            return "/";
        }

        try
        {
            return Uri.UnescapeDataString(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static bool TrySplitService(string path, string suffix, out string service)
    {
        service = string.Empty;
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trimmed = path[..^suffix.Length];
        if (!trimmed.StartsWith('/'))
        {
            return false;
        }

        service = trimmed[1..];
        return service.Length > 0 && !service.Contains('/');
    }

    private HttpResponseData HandleGet(string path)
    {
        if (string.Equals(path, "/description.xml", StringComparison.OrdinalIgnoreCase))
        {
            return Xml(_description);
        }

        if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
        {
            var response = new HttpResponseData
            {
                Body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    status = "ok", port = Port, friendlyName = _settings.FriendlyName,
                    usn = _settings.GetUsn(), version = _settings.Version,
                    renderer = Renderer?.Name, mpv = Renderer?.Available ?? false,
                    protocol = Id
                })
            };
            response.Headers["Content-Type"] = "application/json; charset=utf-8";
            return response;
        }

        if (path.StartsWith("/dlna/", StringComparison.OrdinalIgnoreCase))
        {
            return ServeAsset(path["/dlna/".Length..], path);
        }

        // nirvana.py answers "/" with the settings page; the C# port dropped the web UI.
        return NotFound();
    }

    private static HttpResponseData ServeAsset(string name, string requestPath)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') ||
            name.Contains("..", StringComparison.Ordinal) || name.Contains(':'))
        {
            Log.Warn($"NVA: rejected asset path '{requestPath}'");
            return NotFound();
        }

        var file = Path.Combine(AssetPaths.XmlDir, name);
        if (!File.Exists(file))
        {
            Log.Warn($"NVA: asset not found '{file}'");
            return NotFound();
        }

        var contentType = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".xml" => "text/xml; charset=\"utf-8\"",
            ".csv" => "text/csv; charset=\"utf-8\"",
            ".html" => "text/html; charset=\"utf-8\"",
            _ => "application/octet-stream"
        };

        var response = new HttpResponseData { Body = File.ReadAllBytes(file) };
        response.Headers["Content-Type"] = contentType;
        return response;
    }

    private async Task<HttpResponseData> HandleActionAsync(string service, byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await CallSoapAsync(body, cancellationToken);

            var response = new HttpResponseData { Body = payload };
            response.Headers["Content-Type"] = "text/xml; charset=\"utf-8\"";
            response.Headers["EXT"] = string.Empty;
            return response;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn($"NVA SOAP action {service}/action failed: {ex.Message}");
            var fault = new HttpResponseData
            {
                Status = 500,
                StatusText = "Internal Server Error",
                Body = DlnaProtocol.BuildSoapFault(402, ex.Message)
            };
            fault.Headers["Content-Type"] = "text/xml; charset=\"utf-8\"";
            return fault;
        }
    }

    /// <summary>Routes SOAP by its action namespace; all playback changes share the NVA command lock.</summary>
    public async Task<byte[]> CallSoapAsync(byte[] body, CancellationToken cancellationToken = default)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var action = XDocument.Parse(Encoding.UTF8.GetString(body)).Root?.Element(soap + "Body")?.Elements().FirstOrDefault()
            ?? throw new InvalidDataException("SOAP body has no action element");
        if (action.Name.NamespaceName == NvaActions.ServiceNamespace)
            return await Actions.CallAsync(body, cancellationToken);

        await Casting.Commands.WaitAsync(cancellationToken);
        try
        {
            if (action.Name == XName.Get("SetAVTransportURI", "urn:schemas-upnp-org:service:AVTransport:1"))
            {
                var uri = action.Elements().FirstOrDefault(e => e.Name.LocalName == "CurrentURI")?.Value ?? string.Empty;
                var extension = BilibiliApi.ParseNvaExtension(uri);
                if (uri.StartsWith("bilibili:", StringComparison.OrdinalIgnoreCase) || extension is not null)
                {
                    var client = new NvaClient(Casting, string.Empty, string.Empty);
                    await client.HandlePlayUrlAsync(0, new JsonObject { ["url"] = uri }, cancellationToken);
                    return Encoding.UTF8.GetBytes("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:SetAVTransportURIResponse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"/></s:Body></s:Envelope>");
                }
                await Casting.ReleasePlaybackAsync();
                Renderer?.SetMediaSpeed(1);
                SetState("TransportPlaySpeed", "1");
                SetState("CurrentMediaDuration", "00:00:00");
            }
            return _dlna.Call(body);
        }
        finally { Casting.Commands.Release(); }
    }

    private HttpResponseData HandleEvent(string service, HttpRequestData request, bool unsubscribe)
    {
        var sid = request.Header("SID");
        var callback = request.Header("CALLBACK");
        var timeout = ParseTimeout(request.Header("TIMEOUT"));

        if (unsubscribe)
        {
            if (!string.IsNullOrWhiteSpace(sid) && _dlna.Unsubscribe(sid))
            {
                return Ok();
            }

            Log.Warn($"NVA UNSUBSCRIBE: cannot find sid '{sid}'");
            return Status(412, "Precondition Failed");
        }

        if (!string.IsNullOrWhiteSpace(sid))
        {
            if (!_dlna.Renew(sid, timeout))
            {
                Log.Warn($"NVA SUBSCRIBE renew: cannot find sid '{sid}'");
                return Status(412, "Precondition Failed");
            }

            var renewed = Ok();
            renewed.Headers["SID"] = sid;
            renewed.Headers["TIMEOUT"] = $"Second-{timeout}";
            return renewed;
        }

        if (string.IsNullOrWhiteSpace(callback))
        {
            Log.Warn("NVA SUBSCRIBE: neither SID nor CALLBACK was sent");
            return Status(412, "Precondition Failed");
        }

        try
        {
            var subscription = _dlna.Subscribe(service, callback, timeout, request.RemoteAddress);
            var response = Ok();
            response.Headers["SID"] = subscription.Sid;
            response.Headers["TIMEOUT"] = subscription.Timeout;
            return response;
        }
        catch (Exception ex) when (string.Equals(service, NirvanaControl, StringComparison.Ordinal))
        {
            // python 允许订阅任意 service；NirvanaControl 的状态推送走 NVA 长连接，
            // 这里回一个 SID 让客户端继续工作。
            Log.Debug($"NirvanaControl subscription accepted ({ex.Message}); events use the NVA channel");
            var response = Ok();
            response.Headers["SID"] = $"uuid:{_settings.GetUsn()}";
            response.Headers["TIMEOUT"] = $"Second-{timeout}";
            return response;
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA SUBSCRIBE failed: {ex.Message}");
            return Status(412, "Precondition Failed");
        }
    }

    private static int ParseTimeout(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 1800;
        }

        var value = raw.Trim();
        var index = value.LastIndexOf('-');
        var digits = index >= 0 ? value[(index + 1)..] : value;
        return int.TryParse(digits, out var seconds) && seconds > 0 ? Math.Min(seconds, 86400) : 1800;
    }

    // ---- NVA control channel -------------------------------------------

    private async Task<HttpResponseData?> HandleControlChannelAsync(HttpRequestData request, Stream stream,
        CancellationToken cancellationToken)
    {
        var session = request.Header("Session");
        var uuid = request.Header("UUID");

        var protocol = request.IsNva ? "NVA/1.0" : "HTTP/1.0";
        var head = new StringBuilder();
        head.Append(protocol).Append(" 200 OK\r\n");
        head.Append("Content-Type: text/xml; charset=\"utf-8\"\r\n");
        head.Append("Session: ").Append(session).Append("\r\n");
        head.Append("UUID: ").Append(_settings.GetUsn()).Append("\r\n");
        head.Append("NvaVersion: 1\r\n");
        head.Append("Content-Length: 0\r\n");
        head.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        head.Append("Server: Macast/").Append(_settings.Version).Append(" UPnP/1.0 Macast/")
            .Append(_settings.Version).Append("\r\n");
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        Log.Info($"NVA control channel: {request.Method} {request.Path} session:{Tail(session)} uuid:{Tail(uuid)}");

        await Casting.RunSessionAsync(stream, session, uuid, request.Method, cancellationToken);

        // The stream is ours until the client disconnects; returning null keeps the
        // server from writing a second response on this socket.
        return null;
    }

    private static string Tail(string value) => value.Length <= 4 ? value : value[^4..];

    // ---- responses -----------------------------------------------------

    private static HttpResponseData Ok()
    {
        var response = new HttpResponseData { Body = Array.Empty<byte>() };
        response.Headers["Content-Type"] = "text/xml; charset=\"utf-8\"";
        return response;
    }

    private static HttpResponseData Status(int status, string reason)
    {
        var response = new HttpResponseData { Status = status, StatusText = reason, Body = Array.Empty<byte>() };
        response.Headers["Content-Type"] = "text/html; charset=\"utf-8\"";
        return response;
    }

    private static HttpResponseData NotFound() => Status(404, "Not Found");

    private static HttpResponseData Xml(string content)
    {
        var response = new HttpResponseData { Body = Encoding.UTF8.GetBytes(content) };
        response.Headers["Content-Type"] = "text/xml; charset=\"utf-8\"";
        return response;
    }
}
