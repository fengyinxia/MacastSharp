using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Macast.Core;
using Macast.Core.Plugins;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Cli;

internal static class SelfTest
{
    private const string AvTransportNs = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string RenderingControlNs = "urn:schemas-upnp-org:service:RenderingControl:1";
    private const string ConnectionManagerNs = "urn:schemas-upnp-org:service:ConnectionManager:1";

    public static async Task<int> RunAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "macast-cs-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var previousConfig = Environment.GetEnvironmentVariable("MACAST_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MACAST_CONFIG_DIR", tempDir);

        var failures = new List<string>();
        MacastApp? app = null;
        try
        {
            app = new MacastApp(noMpv: true, loopbackOnly: true);
            await app.StartAsync(0);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{app.Port}"), Timeout = TimeSpan.FromSeconds(5) };

            await CheckAsync("description.xml", async () =>
            {
                var text = await http.GetStringAsync("/description.xml");
                Require(text.Contains("<friendlyName>", StringComparison.Ordinal), "friendlyName missing");
                Require(text.Contains("urn:schemas-upnp-org:device:MediaRenderer:1", StringComparison.Ordinal),
                    "MediaRenderer missing");
            }, failures);

            await CheckAsync("web settings UI removed", async () =>
            {
                using var root = await http.GetAsync("/");
                Require(root.StatusCode == HttpStatusCode.NotFound,
                    "GET / should be 404, got " + (int)root.StatusCode);

                using var api = await http.GetAsync("/api?query=log");
                Require(api.StatusCode == HttpStatusCode.NotFound,
                    "GET /api should be 404, got " + (int)api.StatusCode);

                using var post = await http.PostAsync("/api", new StringContent("query=log"));
                Require(post.StatusCode == HttpStatusCode.NotFound,
                    "POST /api should be 404, got " + (int)post.StatusCode);

                var scpd = await http.GetStringAsync("/dlna/AVTransport.xml");
                Require(scpd.Contains("SetAVTransportURI", StringComparison.Ordinal),
                    "SCPD still has to be served from /dlna");
            }, failures);

            await CheckAsync("plugin registry", () =>
            {
                Require(PluginRegistry.RendererPlugins.Count == 2,
                    "expected exactly 2 renderer plugins (MPV, PotPlayer)");
                Require(PluginRegistry.ProtocolPlugins.Count == 1, "expected one automatic protocol service");
                Require(PluginRegistry.FindRenderer("MPV") is not null, "MPV plugin missing");
                Require(PluginRegistry.FindRenderer("PotPlayer") is not null, "PotPlayer plugin missing");
                Require(PluginRegistry.FindRenderer("Live") is null, "Live plugin should be removed");
                var legacyLive = new AppSettings();
                legacyLive.Set("Macast_Renderer", "Live");
                Require(PluginRegistry.ResolveRenderer(legacyLive).Metadata.Id == "MPV", "legacy Live config should fall back to MPV");
                Require(PluginRegistry.FindRenderer("IINA") is null, "IINA plugin should be removed");
                Require(PluginRegistry.FindRenderer("Web") is null, "Web plugin should be removed");
                Require(PluginRegistry.FindRenderer("PIFM") is null, "PIFMRDS plugin should be removed");
                Require(PluginRegistry.FindProtocol("NVA") is not null, "NVA protocol plugin missing");
                Require(PluginRegistry.ResolveRenderer(AppSettings.Load()).Metadata.Id == "MPV",
                    "default renderer should be MPV");
                Require(PluginRegistry.ResolveProtocol(AppSettings.Load()).Metadata.Id == "Auto",
                    "default protocol should be automatic");
                foreach (var legacy in new[] { "DLNA", "NVA", "Auto" })
                {
                    var settings = new AppSettings();
                    settings.Set("Macast_Protocol", legacy);
                    Require(PluginRegistry.ResolveProtocol(settings).Metadata.Id == "Auto", "legacy protocol not mapped: " + legacy);
                }

                var potPlayer = PluginRegistry.FindRenderer("PotPlayer")!.Metadata;
                Require(potPlayer.Platforms.Contains("win32"), "PotPlayer should declare win32");
                return Task.CompletedTask;
            }, failures);

            await CheckAsync("settings round trip", () =>
            {
                var settings = AppSettings.Load();
                settings.Set("Macast_Renderer", "PotPlayer");
                settings.Set("Macast_Protocol", "NVA");
                settings.Set("PluginRoundTrip", 42);
                settings.Save();

                var reloaded = AppSettings.Load();
                Require(reloaded.GetString("Macast_Renderer") == "PotPlayer", "renderer key did not persist");
                Require(reloaded.GetString("Macast_Protocol") == "NVA", "protocol key did not persist");
                Require(reloaded.GetInt("PluginRoundTrip") == 42, "user key did not persist");

                reloaded.Set("Macast_Renderer", "MPV");
                reloaded.Set("Macast_Protocol", "DLNA");
                reloaded.Save();
                return Task.CompletedTask;
            }, failures);

            await CheckAsync("health", async () =>
            {
                var text = await http.GetStringAsync("/health");
                using var json = JsonDocument.Parse(text);
                Require(json.RootElement.GetProperty("status").GetString() == "ok", "status != ok");
                Require(json.RootElement.GetProperty("port").GetInt32() == app.Port, "port mismatch");
            }, failures);

            await CheckAsync("SetAVTransportURI", async () =>
            {
                var response = await SoapAsync(http, "/AVTransport/action",
                    AvTransportNs, "SetAVTransportURI",
                    "<InstanceID>0</InstanceID>" +
                    "<CurrentURI>http://127.0.0.1:9/test.mp4</CurrentURI>" +
                    "<CurrentURIMetaData></CurrentURIMetaData>");
                Require(response.Contains("SetAVTransportURIResponse", StringComparison.Ordinal),
                    "SetAVTransportURIResponse missing");
            }, failures);

            await CheckAsync("Play", async () =>
            {
                var response = await SoapAsync(http, "/AVTransport/action",
                    AvTransportNs, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");
                Require(response.Contains("PlayResponse", StringComparison.Ordinal), "PlayResponse missing");
            }, failures);

            await CheckAsync("GetTransportInfo", async () =>
            {
                var response = await SoapAsync(http, "/AVTransport/action",
                    AvTransportNs, "GetTransportInfo", "<InstanceID>0</InstanceID>");
                Require(response.Contains("<CurrentTransportState>PLAYING</CurrentTransportState>", StringComparison.Ordinal),
                    "expected PLAYING");
            }, failures);

            await CheckAsync("GetPositionInfo", async () =>
            {
                var response = await SoapAsync(http, "/AVTransport/action",
                    AvTransportNs, "GetPositionInfo", "<InstanceID>0</InstanceID>");
                Require(response.Contains("test.mp4", StringComparison.Ordinal), "TrackURI missing");
            }, failures);

            await CheckAsync("RenderingControl", async () =>
            {
                var volume = await SoapAsync(http, "/RenderingControl/action",
                    RenderingControlNs, "GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
                Require(volume.Contains("<CurrentVolume>", StringComparison.Ordinal), "CurrentVolume missing");

                var set = await SoapAsync(http, "/RenderingControl/action",
                    RenderingControlNs, "SetVolume",
                    "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>42</DesiredVolume>");
                Require(set.Contains("SetVolumeResponse", StringComparison.Ordinal), "SetVolumeResponse missing");
            }, failures);

            await CheckAsync("ConnectionManager", async () =>
            {
                var response = await SoapAsync(http, "/ConnectionManager/action",
                    ConnectionManagerNs, "GetProtocolInfo", string.Empty);
                Require(response.Contains("<Sink>", StringComparison.Ordinal), "Sink missing");
            }, failures);

            await CheckAsync("GENA initial + state event", async () =>
            {
                await using var probe = new EventProbe();
                using var subscribe = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"),
                    "/RenderingControl/event");
                subscribe.Headers.TryAddWithoutValidation("CALLBACK",
                    $"<http://127.0.0.1:{probe.Port}/event>");
                subscribe.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
                using var subscribed = await http.SendAsync(subscribe);
                Require(subscribed.StatusCode == HttpStatusCode.OK, "SUBSCRIBE status " + (int)subscribed.StatusCode);
                Require(subscribed.Headers.TryGetValues("SID", out var sids), "SID missing");
                var sid = sids!.FirstOrDefault() ?? string.Empty;

                var initial = await probe.NextAsync(TimeSpan.FromSeconds(5));
                Require(initial.Method == "NOTIFY", "initial method " + initial.Method);
                Require(initial.Headers.TryGetValue("SID", out var initialSid) && initialSid == sid,
                    "initial SID mismatch");
                Require(initial.Body.Contains("propertyset", StringComparison.Ordinal),
                    "initial propertyset missing");
                Require(initial.Body.Contains("Volume", StringComparison.Ordinal),
                    "initial Volume missing");

                var set = await SoapAsync(http, "/RenderingControl/action",
                    RenderingControlNs, "SetVolume",
                    "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>37</DesiredVolume>");
                Require(set.Contains("SetVolumeResponse", StringComparison.Ordinal), "SetVolumeResponse missing");

                var changed = await probe.NextAsync(TimeSpan.FromSeconds(5));
                Require(changed.Body.Contains("37", StringComparison.Ordinal),
                    "state event does not contain volume 37: " + changed.Body);
                Require(changed.Headers.TryGetValue("SEQ", out var seq) && seq == "1",
                    "SEQ should be 1 after initial event, got " + seq);

                using var unsubscribe = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"),
                    "/RenderingControl/event");
                unsubscribe.Headers.TryAddWithoutValidation("SID", sid);
                using var unsubscribed = await http.SendAsync(unsubscribe);
                Require(unsubscribed.StatusCode == HttpStatusCode.OK, "UNSUBSCRIBE status " + (int)unsubscribed.StatusCode);
            }, failures);

            await CheckAsync("SUBSCRIBE/UNSUBSCRIBE", async () =>
            {
                using var subscribe = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"),
                    $"/AVTransport/event");
                subscribe.Headers.TryAddWithoutValidation("CALLBACK", "<http://127.0.0.1:9/event>");
                subscribe.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
                using var subscribed = await http.SendAsync(subscribe);
                Require(subscribed.StatusCode == HttpStatusCode.OK, "SUBSCRIBE status " + (int)subscribed.StatusCode);
                Require(subscribed.Headers.TryGetValues("SID", out var sids), "SID missing");
                var sid = sids!.FirstOrDefault() ?? string.Empty;
                Require(sid.StartsWith("uuid:", StringComparison.Ordinal), "SID format invalid");

                using var unsubscribe = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"),
                    $"/AVTransport/event");
                unsubscribe.Headers.TryAddWithoutValidation("SID", sid);
                using var unsubscribed = await http.SendAsync(unsubscribe);
                Require(unsubscribed.StatusCode == HttpStatusCode.OK, "UNSUBSCRIBE status " + (int)unsubscribed.StatusCode);
            }, failures);

            await BoundaryTests.RunAsync(app, http, failures);
            await NvaTests.RunAsync(failures);

            await CheckAsync("restart lifecycle", async () =>
            {
                var runningApp = app ?? throw new InvalidOperationException("app is null");
                await runningApp.StopAsync();
                await runningApp.StartAsync(0);
                using var restarted = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{runningApp.Port}"),
                    Timeout = TimeSpan.FromSeconds(5)
                };
                var health = await restarted.GetStringAsync("/health");
                Require(health.Contains("\"status\":\"ok\"", StringComparison.Ordinal),
                    "health after restart: " + health);
            }, failures);

            await CheckAsync("renderer reload + restart API", async () =>
            {
                var running = app ?? throw new InvalidOperationException("app is null");
                var before = running.Renderer;
                await running.ReloadRendererAsync();
                Require(!ReferenceEquals(before, running.Renderer), "ReloadRenderer did not recreate the renderer");
                Require(ReferenceEquals(running.Protocol.Renderer, running.Renderer),
                    "protocol.Renderer was not updated");

                await running.RestartAsync();
                using var after = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{running.Port}"),
                    Timeout = TimeSpan.FromSeconds(5)
                };
                var health = await after.GetStringAsync("/health");
                Require(health.Contains("\"status\":\"ok\"", StringComparison.Ordinal),
                    "health after RestartAsync: " + health);
                Require(running.Running, "app is not running after RestartAsync");
            }, failures);

            await CheckAsync("environment plugin override", () =>
            {
                try
                {
                    Environment.SetEnvironmentVariable("MACAST_RENDERER", "PotPlayer");
                    Environment.SetEnvironmentVariable("MACAST_PROTOCOL", "DLNA");
                    var settings = AppSettings.Load();
                    Require(PluginRegistry.ResolveRenderer(settings).Metadata.Id == "PotPlayer",
                        "MACAST_RENDERER override ignored");
                    Require(PluginRegistry.ResolveProtocol(settings).Metadata.Id == "Auto",
                        "legacy MACAST_PROTOCOL should resolve to automatic mode");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("MACAST_RENDERER", null);
                    Environment.SetEnvironmentVariable("MACAST_PROTOCOL", null);
                }

                return Task.CompletedTask;
            }, failures);

            await CheckAsync("NVA protocol service", async () =>
            {
                var plugin = PluginRegistry.FindProtocol("NVA")
                             ?? throw new InvalidOperationException("NVA plugin missing");
                var service = plugin.Create(new ProtocolContext(AppSettings.Load(), loopbackOnly: true));
                await service.StartAsync(0);
                try
                {
                    using var nvaHttp = new HttpClient
                    {
                        BaseAddress = new Uri($"http://127.0.0.1:{service.Port}"),
                        Timeout = TimeSpan.FromSeconds(5)
                    };

                    var description = await nvaHttp.GetStringAsync("/description.xml");
                    Require(description.Contains("urn:app-bilibili-com:service:NirvanaControl:3", StringComparison.Ordinal),
                        "NVA service entry missing from description.xml");
                    Require(description.Contains("Bilibili Inc.", StringComparison.Ordinal),
                        "bilibili branding missing");

                    var scpd = await nvaHttp.GetStringAsync("/dlna/NirvanaControl.xml");
                    Require(scpd.Contains("GetAppInfo", StringComparison.Ordinal), "NirvanaControl SCPD not served");

                    var body = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                               "<s:Body><u:GetAppInfo xmlns:u=\"urn:app-bilibili-com:service:NirvanaControl:3\"/>" +
                               "</s:Body></s:Envelope>";
                    using var action = new StringContent(body, Encoding.UTF8, "text/xml");
                    var actionResponse = await nvaHttp.PostAsync("/NirvanaControl/action", action);
                    var actionText = await actionResponse.Content.ReadAsStringAsync();
                    Require(actionText.Contains("GetAppInfoResponse", StringComparison.Ordinal),
                        "NirvanaControl SOAP action failed: " + actionText);
                    Require(actionText.Contains("<CurrentSignedIn>0</CurrentSignedIn>", StringComparison.Ordinal),
                        "a device without an access key must not report as signed in: " + actionText);

                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, service.Port);
                    await using var stream = tcp.GetStream();
                    var handshake = "SETUP / NVA/1.0\r\nSession: selftest\r\nUUID: selftest\r\nContent-Length: 0\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(handshake);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();

                    var buffer = new byte[1024];
                    using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var read = await stream.ReadAsync(buffer, readTimeout.Token);
                    var head = Encoding.ASCII.GetString(buffer, 0, read);
                    Require(head.StartsWith("NVA/1.0 200 OK", StringComparison.Ordinal),
                        "NVA handshake failed: " + head.Replace("\r\n", " | "));
                    Require(head.Contains("NvaVersion: 1", StringComparison.Ordinal), "NvaVersion header missing");
                }
                finally
                {
                    await service.DisposeAsync();
                }
            }, failures);

            await CheckAsync("NVA SSDP service discovery", async () =>
            {
                using var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var port = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
                portProbe.Dispose();
                await using var ssdp = new Macast.Core.Ssdp.SsdpServer(new AppSettings(), port)
                { AdvertiseNva = true };
                await ssdp.StartAsync(12345, new[]
                { new Macast.Core.Net.InterfaceAddress("loopback", "loopback", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), false) });
                foreach (var target in new[] { "urn:app-bilibili-com:service:NirvanaControl:3", "urn:app-bilibili-com:service:NirvanaControl:*" })
                {
                    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    var request = $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: {target}\r\n\r\n";
                    await udp.SendAsync(Encoding.UTF8.GetBytes(request), new IPEndPoint(IPAddress.Loopback, ssdp.SsdpPort));
                    var receive = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    var response = Encoding.UTF8.GetString(receive.Buffer);
                    Require(response.Contains("ST: urn:app-bilibili-com:service:NirvanaControl:3\r\n", StringComparison.Ordinal), "NVA SSDP service missing");
                }
            }, failures);

            await CheckAsync("SSDP M-SEARCH", async () =>
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var request = "M-SEARCH * HTTP/1.1\r\n" +
                              "HOST: 239.255.255.250:1900\r\n" +
                              "MAN: \"ssdp:discover\"\r\n" +
                              "MX: 1\r\n" +
                              "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n";
                await udp.SendAsync(Encoding.UTF8.GetBytes(request),
                    new IPEndPoint(IPAddress.Loopback, app.Ssdp.SsdpPort));
                var receive = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                var text = Encoding.UTF8.GetString(receive.Buffer);
                Require(text.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal),
                    "SSDP response status invalid");
                Require(text.Contains("LOCATION:", StringComparison.Ordinal), "LOCATION missing");
                Require(text.Contains("MediaRenderer", StringComparison.Ordinal), "ST missing");
            }, failures);
        }
        catch (Exception ex)
        {
            failures.Add("fatal: " + ex);
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    await app.DisposeAsync();
                }
                catch (Exception ex)
                {
                    failures.Add("stop: " + ex.Message);
                }
            }

            Environment.SetEnvironmentVariable("MACAST_CONFIG_DIR", previousConfig);
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("SELFTEST PASS");
            return 0;
        }

        Console.WriteLine($"SELFTEST FAIL ({failures.Count})");
        foreach (var failure in failures)
        {
            Console.WriteLine("  - " + failure);
        }

        return 1;
    }

    internal static async Task CheckAsync(string name, Func<Task> action, List<string> failures)
    {
        try
        {
            await action();
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"FAIL  {name}: {ex.Message}");
        }
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task<string> SoapAsync(HttpClient http, string path,
        string serviceNamespace, string action, string arguments)
    {
        var body = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                   "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                   "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   "<s:Body>" +
                   $"<u:{action} xmlns:u=\"{serviceNamespace}\">{arguments}</u:{action}>" +
                   "</s:Body></s:Envelope>";
        using var content = new StringContent(body, Encoding.UTF8, "text/xml");
        using var response = await http.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();
        Require(response.IsSuccessStatusCode,
            $"HTTP {(int)response.StatusCode}: {text}");
        return text;
    }
}
