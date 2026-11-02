using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Macast.Core;
using Macast.Core.Protocols;
using Macast.Core.Protocols.Nva;
using Macast.Core.Server;
using Macast.Core.Settings;
using Macast.Core.Utils;
using static Macast.Cli.SelfTest;

namespace Macast.Cli;

internal static class BoundaryTests
{
    public static async Task RunAsync(MacastApp app, HttpClient http, List<string> failures)
    {
        await CheckAsync("loopback listener binding", () =>
        {
            RequireLoopback(app.Port);
            return Task.CompletedTask;
        }, failures);

        await CheckAsync("HTTP HEAD, paths and chunked SOAP", async () =>
        {
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/description.xml"));
            Require(head.IsSuccessStatusCode && head.Content.Headers.ContentLength > 0, "HEAD lost content headers");
            Require((await head.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD returned a body");
            using var invalidPath = await http.GetAsync("/dlna/..%5cDescription.xml");
            Require(invalidPath.StatusCode == HttpStatusCode.NotFound, "asset traversal accepted");
            var soap = Encoding.UTF8.GetBytes("""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:GetTransportInfo xmlns:u="urn:schemas-upnp-org:service:AVTransport:1"><InstanceID>0</InstanceID></u:GetTransportInfo></s:Body></s:Envelope>""");
            using var post = new HttpRequestMessage(HttpMethod.Post, "/AVTransport/action") { Content = new ByteArrayContent(soap) };
            post.Headers.TransferEncodingChunked = true;
            post.Headers.ExpectContinue = true;
            using var response = await http.SendAsync(post);
            Require(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("GetTransportInfoResponse"), "chunked SOAP failed");
            using var bad = await http.PostAsync("/AVTransport/action", new StringContent("not xml"));
            Require(bad.StatusCode == HttpStatusCode.InternalServerError && (await bad.Content.ReadAsStringAsync()).Contains("UPnPError"), "SOAP fault lost");
        }, failures);

        await CheckAsync("GENA renewal over socket HTTP", async () =>
        {
            await using var probe = new EventProbe();
            using var subscribe = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), "/AVTransport/event");
            subscribe.Headers.TryAddWithoutValidation("CALLBACK", $"<http://127.0.0.1:{probe.Port}/renew>");
            using var created = await http.SendAsync(subscribe);
            Require(created.IsSuccessStatusCode, "subscription rejected");
            var sid = created.Headers.GetValues("SID").Single();
            await probe.NextAsync(TimeSpan.FromSeconds(3));
            using var renew = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), "/AVTransport/event");
            renew.Headers.TryAddWithoutValidation("SID", sid);
            renew.Headers.TryAddWithoutValidation("TIMEOUT", "Second-60");
            using var renewed = await http.SendAsync(renew);
            Require(renewed.IsSuccessStatusCode && renewed.Headers.GetValues("TIMEOUT").Single() == "Second-60", "renewal failed");
            using var remove = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), "/AVTransport/event");
            remove.Headers.TryAddWithoutValidation("SID", sid);
            using var removed = await http.SendAsync(remove);
            Require(removed.IsSuccessStatusCode, "unsubscribe failed");
        }, failures);

        await CheckAsync("settings concurrent atomic save", async () =>
        {
            var settings = AppSettings.Load();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                settings.Set("Concurrent" + i, i);
                settings.Save();
                using var document = JsonDocument.Parse(File.ReadAllText(AppSettings.SettingPath));
            })));
            var loaded = AppSettings.Load();
            Require(Enumerable.Range(0, 8).All(i => loaded.GetInt("Concurrent" + i, -1) == i),
                "concurrent values lost");
            Require(!Directory.EnumerateFiles(AppSettings.ConfigDir, "*.tmp").Any(), "temporary file left behind");
        }, failures);

        await CheckAsync("settings save reports failure", () =>
        {
            var config = AppSettings.ConfigDir;
            var blocked = Path.Combine(config, "not-a-directory");
            File.WriteAllText(blocked, "test");
            try
            {
                Environment.SetEnvironmentVariable("MACAST_CONFIG_DIR", blocked);
                try { new AppSettings().Save(); }
                catch (IOException) { return Task.CompletedTask; }
                throw new InvalidOperationException("save failure was swallowed");
            }
            finally { Environment.SetEnvironmentVariable("MACAST_CONFIG_DIR", config); }
        }, failures);

        await CheckAsync("log redaction and debug level", () =>
        {
            var output = Console.Out;
            var echo = Log.Echo;
            var debug = Log.DebugEnabled;
            using var capture = new StringWriter();
            try
            {
                Console.SetOut(capture);
                Log.Echo = true;
                Log.DebugEnabled = false;
                Log.Info("access_key=test-secret&token=test-secret {\"accessKey\":\"test-secret\"}");
                Log.Debug("debug-hidden");
                Require(!capture.ToString().Contains("test-secret"), "credential leaked");
                Require(!capture.ToString().Contains("debug-hidden"), "debug enabled by default");
            }
            finally
            {
                Console.SetOut(output);
                Log.Echo = echo;
                Log.DebugEnabled = debug;
            }
            return Task.CompletedTask;
        }, failures);

        await CheckAsync("GENA callback restriction", async () =>
        {
            using var subscribe = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), "/AVTransport/event");
            subscribe.Headers.TryAddWithoutValidation("CALLBACK", "<http://192.0.2.1:9/event>");
            using var response = await http.SendAsync(subscribe);
            Require(response.StatusCode == HttpStatusCode.PreconditionFailed, "foreign callback accepted");
        }, failures);

        await CheckAsync("GENA subscription limit", async () =>
        {
            var protocol = ((DlnaProtocolService)app.Protocol).Protocol;
            await using var probe = new EventProbe();
            var sids = new List<string>();
            try
            {
                for (var i = 0; i < 8; i++)
                    sids.Add(protocol.Subscribe("AVTransport", $"<http://127.0.0.1:{probe.Port}/{i}>", 300, IPAddress.Loopback).Sid);
                for (var i = 0; i < 8; i++)
                    await probe.NextAsync(TimeSpan.FromSeconds(3));
                try { protocol.Subscribe("AVTransport", $"<http://127.0.0.1:{probe.Port}/overflow>", 300, IPAddress.Loopback); }
                catch (InvalidDataException) { return; }
                throw new InvalidOperationException("subscription limit missing");
            }
            finally { foreach (var sid in sids) protocol.Unsubscribe(sid); }
        }, failures);

        await CheckAsync("concurrent lifecycle operations", async () =>
        {
            await Task.WhenAll(app.StopAsync(), app.StartAsync(), app.ReloadRendererAsync());
            Require(app.Running && ReferenceEquals(app.Protocol.Renderer, app.Renderer), "lifecycle operations overlapped");
        }, failures);

        await CheckAsync("NVA request limits and truncated body", async () =>
        {
            var handled = 0;
            await using var server = new SocketHttpServer((request, stream, token) =>
            {
                Interlocked.Increment(ref handled);
                return Task.FromResult<HttpResponseData?>(new HttpResponseData());
            }, loopbackOnly: true);
            await server.StartAsync(0);
            RequireLoopback(server.Port);
            foreach (var length in new[] { "-1", "2147483647", "invalid" })
                await ExpectClosedAsync(server.Port, $"POST / HTTP/1.0\r\nContent-Length: {length}\r\n\r\n");
            await ExpectClosedAsync(server.Port, "POST / HTTP/1.0\r\nContent-Length: 3\r\n\r\nx", halfClose: true);
            await ExpectClosedAsync(server.Port, "GET / HTTP/1.0\r\nX: " + new string('x', 8192) + "\r\n\r\n");
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync($"http://127.0.0.1:{server.Port}/");
            Require(response.IsSuccessStatusCode && handled == 1, "invalid request reached handler or listener stopped");
        }, failures);

        await CheckAsync("NVA fragmented frames and frame limits", async () =>
        {
            await using var service = new NvaProtocolService(new AppSettings(), loopbackOnly: true);
            var client = new NvaClient(service.Casting, "boundary", "boundary");
            // RET frame with a JSON body, split at every byte boundary.
            var frame = new byte[] { 0xc0, 1, 0, 0, 0, 1, 0, 0, 0, 2, 123, 125 };
            foreach (var value in frame) await client.ParseAsync(new byte[] { value }, CancellationToken.None);
            foreach (var length in new[] { -1, int.MaxValue })
            {
                var malformed = frame[..10];
                BinaryPrimitives.WriteInt32BigEndian(malformed.AsSpan(6), length);
                var invalid = new NvaClient(service.Casting, "bad", "bad");
                try { await invalid.ParseAsync(malformed, CancellationToken.None); }
                catch (InvalidDataException) { continue; }
                throw new InvalidOperationException("invalid frame length accepted");
            }
        }, failures);

        await CheckAsync("NVA stop closes pending clients", async () =>
        {
            await using var server = new SocketHttpServer((request, stream, token) =>
                Task.FromResult<HttpResponseData?>(new HttpResponseData()), loopbackOnly: true);
            await server.StartAsync(0);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /"));
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }, failures);
    }

    private static void RequireLoopback(int port)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == port).ToArray();
        Require(listeners.Length > 0 && listeners.All(e => IPAddress.IsLoopback(e.Address)), "listener exposed on non-loopback address");
    }

    private static async Task ExpectClosedAsync(int port, string request, bool halfClose = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        if (halfClose) client.Client.Shutdown(SocketShutdown.Send);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var buffer = new byte[128];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            var response = Encoding.ASCII.GetString(buffer, 0, read);
            Require(read == 0 || response.StartsWith("HTTP/1.1 4", StringComparison.Ordinal),
                "invalid request received a successful response");
        }
        catch (IOException) { /* Reset is also a rejected request. */ }
    }
}
