using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Macast.Core.Plugins;
using Macast.Core.Protocols.Nva;
using Macast.Core.Renderer;
using Macast.Core.Settings;
using static Macast.Cli.SelfTest;

namespace Macast.Cli;

internal static class NvaTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    public static async Task RunAsync(List<string> failures)
    {
        await CheckAsync("NVA TV name and disabled danmaku compatibility", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            service.Settings.Set("DLNA_FriendlyName", "Macast & PC");
            service.RefreshDescription();
            var xml = System.Xml.Linq.XDocument.Parse(service.Description);
            System.Xml.Linq.XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            var device = xml.Root!.Element(ns + "device")!;
            Require(device.Element(ns + "friendlyName")!.Value == "我的小电视 · Macast & PC", "Android TV name marker or escaping missing");
            Require(service.Settings.FriendlyName == "Macast & PC", "NVA changed stored DLNA name");
            Require(!Macast.Core.Upnp.DeviceDescription.Build(service.Settings).Contains("我的小电视", StringComparison.Ordinal), "TV branding leaked into DLNA");
            service.Settings.Set("DLNA_FriendlyName", "我的小电视 · living room");
            service.RefreshDescription();
            Require(service.Description.Contains("<friendlyName>我的小电视 · living room</friendlyName>", StringComparison.Ordinal), "TV name marker duplicated");
            var client = Attach(service.Casting, "danmaku");
            service.SetState("DisplayCurrentSubtitle", "true");
            await client.HandleCommandAsync("SwitchDanmaku", 1, new JsonObject { ["open"] = false }, Token);
            await client.HandleCommandAsync("SwitchDanmaku", 2, new JsonObject { ["open"] = true }, Token);
            await client.HandleCommandAsync("SendDanmaku", 3, new JsonObject { ["content"] = "ignored" }, Token);
            await service.Actions.CallAsync(Soap("SetDanmakuSwitch", "<DesiredSwitch>false</DesiredSwitch>"), Token);
            await service.Actions.CallAsync(Soap("AppendDanmaku", "<Content>ignored</Content>"), Token);
            Require(service.GetState("DisplayCurrentSubtitle") == "true", "danmaku command changed normal subtitle state");
            Require(!client.DescribePlayInfoTotal()["danmakuState"]!.GetValue<bool>(), "removed danmaku advertised as enabled");
            var info = Encoding.UTF8.GetString(await service.Actions.CallAsync(Soap("GetDanmaku"), Token));
            Require(!info.Contains("macast.ass", StringComparison.Ordinal), "legacy subtitle path exposed");
        }, failures);

        await CheckAsync("NVA shared playback and reconnect", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            var first = Attach(service.Casting, "first");
            var second = Attach(service.Casting, "second");
            await second.HandleCommandAsync("Play", 1, Play("200"), Token);
            Require(first.DescribePlay()["cid"]!.GetValue<string>() == "200", "parallel connection has stale state");
            Require(service.Casting.DescribeActivePlay()["cid"]!.GetValue<string>() == "200", "manager selected stale connection");
            first.Terminate();
            second.Terminate();
            var restored = Attach(service.Casting, "first");
            Require(restored.DescribePlay()["cid"]!.GetValue<string>() == "200", "reconnect lost playback");
            Require(await service.Casting.ApplyQualityAsync(80, Token), "quality switch failed after reconnect");
            Require(fixture.LastCid == "200", "quality request used wrong video");
        }, failures);

        await CheckAsync("NVA TV identity and credential survive 1080P switch", async () =>
        {
            foreach (var dash in new[] { false, true })
            {
                using var fixture = new Fixture();
                await using var service = fixture.CreateService();
                // No real player is launched: null exercises the non-DASH request branch.
                using var renderer = dash
                    ? new MpvRenderer(new RendererContext(service.Settings, service, true))
                    : null;
                service.Renderer = renderer;
                var client = Attach(service.Casting, "quality");
                var play = Play("500");
                play["userDesireQn"] = 64;
                play["accessKey"] = "fake-phone-quality";
                await client.HandleCommandAsync("Play", 1, play, Token);
                await client.HandleCommandAsync("SwitchQn", 2, new JsonObject { ["qn"] = 80 }, Token);
                var query = fixture.LastQuery;
                Require(query["platform"] == "android_tv_yst" && query["mobi_app"] == "android_tv_yst" && query["build"] == "106400", "TV identity regressed");
                Require(query["qn"] == "80" && query["access_key"] == "fake-phone-quality" && query["mobile_access_key"] == "fake-phone-quality", "quality switch lost credential or requested quality");
                Require(query.ContainsKey("fnval") == dash, "renderer format mismatch");
                Require(client.DescribePlay()["curQn"]!.GetValue<int>() == 80, "granted quality not propagated");
            }
        }, failures);

        await CheckAsync("PotPlayer requests DASH and opens both streams", async () =>
        {
            using var fixture = new Fixture { DashResponse = true };
            await using var service = fixture.CreateService();
            using var renderer = new PotPlayerRenderer(new RendererContext(service.Settings, service, true));
            service.Renderer = renderer;
            var client = Attach(service.Casting, "potplayer-dash");
            client.ApplyPlayItem(new NvaPlayItem("123", "123", "500", "0", "test", false));
            var resolved = await client.ResolvePlayUrlAsync(Token);
            Require(fixture.LastQuery["fnval"] == "16" && fixture.LastQuery["fnver"] == "0", "PotPlayer did not request DASH");
            Require(fixture.LastQuery["platform"] == "android_tv_yst", "TV identity changed");
            Require(resolved?.CurrentQn == 80 && resolved.AudioUrl is { Length: > 0 }, "DASH audio or quality lost");
            var method = typeof(PotPlayerRenderer).GetMethod("BuildStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var info = (System.Diagnostics.ProcessStartInfo)method.Invoke(renderer,
                new object[] { "PotPlayerMini64.exe", resolved!.Url!, 42d, resolved.AudioUrl! })!;
            Require(info.ArgumentList[0] == resolved.Url && info.ArgumentList[1] == resolved.AudioUrl, "stream arguments incomplete");
            Require(info.ArgumentList.Contains("/same") && info.ArgumentList.Contains("/seek=0:00:42"), "combined playback or resume argument missing");
            Require(info.ArgumentList.Contains("/new") && info.ArgumentList.Any(a => a.StartsWith("/volume=")), "dedicated instance or initial volume missing");
            Require(!info.ArgumentList.Any(a => a.StartsWith("/sub=", StringComparison.Ordinal)), "legacy danmaku loaded by PotPlayer");
        }, failures);

        await CheckAsync("NVA speed survives quality change without danmaku", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            using var renderer = new MpvRenderer(new RendererContext(service.Settings, service, true));
            service.Renderer = renderer;
            renderer.Start();
            var client = Attach(service.Casting, "speed");
            var play = Play("300");
            play["danmakuSwitchSave"] = true;
            await client.HandleCommandAsync("Play", 1, play, Token);
            Require(!client.DescribePlayInfoTotal()["danmakuState"]!.GetValue<bool>(), "phone enabled removed danmaku");
            await client.HandleCommandAsync("SwitchSpeed", 2, new JsonObject { ["speed"] = 1.5 }, Token);
            await client.HandleCommandAsync("SwitchQn", 3, new JsonObject { ["qn"] = 80 }, Token);
            Require(service.GetState("TransportPlaySpeed") == "1.5", "quality switch reset speed");
            Require(!client.DescribePlayInfoTotal()["danmakuState"]!.GetValue<bool>(), "quality switch enabled danmaku");
            await service.Casting.StopBackgroundAsync();
            Require(fixture.DanmakuRequests == 0, "playback still downloads danmaku");
        }, failures);

        await CheckAsync("NVA episode identifiers reset", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            var client = Attach(service.Casting, "episodes");
            client.ApplyPlayItem(new NvaPlayItem("1", "111", "2", "111", "episode", false));
            client.ApplyPlayItem(new NvaPlayItem("222", "333", "444", "0", "normal", false));
            var state = client.DescribePlay();
            Require(state["oid"]!.GetValue<string>() == "333", "item oid ignored");
            Require(state["epId"]!.GetValue<int>() == 0 && state["playUrlType"]!.GetValue<int>() == 1, "episode type leaked");
        }, failures);

        await CheckAsync("NVA legacy extension and video reset", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            var client = Attach(service.Casting, "legacy");
            var payload = new JsonObject
            {
                ["ver"] = 2,
                ["content"] = new JsonObject { ["aid"] = "10", ["cid"] = "20", ["sk"] = "12", ["ct"] = 2 }
            };
            foreach (var key in new[] { "nva_ext", "_nva_ext_" })
                Require(BilibiliApi.ParseNvaExtension("bilibili://video?" + key + "=" + Uri.EscapeDataString(payload.ToJsonString())) is not null, "extension not parsed");
            var live = Play("live");
            live["roomId"] = "123";
            await client.HandleCommandAsync("Play", 1, live, Token);
            await client.HandleCommandAsync("PlayUrl", 2, new JsonObject
            { ["url"] = "bilibili://video?_nva_ext_=" + Uri.EscapeDataString(payload.ToJsonString()) }, Token);
            Require(client.DescribePlay()["roomId"]!.GetValue<string>() == "0", "live room leaked into video");
            Require(fixture.LastCid == "20", "legacy content not resolved");
        }, failures);

        await CheckAsync("automatic DLNA and NVA takeover on one listener", async () =>
        {
            using var fixture = new Fixture { DelayOldMetadata = true };
            await using var service = fixture.CreateService();
            using var renderer = new MpvRenderer(new RendererContext(service.Settings, service, true));
            service.Renderer = renderer;
            await service.StartAsync(0);
            renderer.Start();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{service.Port}") };
            var port = service.Port;
            var client = Attach(service.Casting, "mixed");
            var old = Play("old");
            old["aid"] = "old";
            old["contentType"] = 1;
            old["userDesireSpeed"] = 1.5;
            await client.HandleCommandAsync("Play", 1, old, Token);
            await fixture.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            service.Casting.SetPlayList(new[]
            { new NvaPlayItem("1", "1", "old", "0", "old", false), new NvaPlayItem("2", "2", "stale", "0", "stale", false) }, 0);
            await StandardSoapAsync(http, "SetAVTransportURI", "<CurrentURI>https://example.invalid/dlna.mp4</CurrentURI><CurrentURIMetaData/>");
            await StandardSoapAsync(http, "Play", "<Speed>1</Speed>");
            Require(fixture.OldCancelled, "DLNA did not cancel old NVA metadata");
            Require(client.DescribePlay()["cid"]!.GetValue<string>() == "", "DLNA kept old Bilibili video");
            Require(!await service.Casting.ApplyQualityAsync(80, Token), "stale quality request can steal DLNA playback");
            await client.HandleCommandAsync("Pause", 2, null, Token);
            Require(service.GetState("TransportState") == "PLAYING", "stale NVA pause affected DLNA");
            Require(service.GetState("TransportPlaySpeed") == "1", "DLNA inherited NVA speed");
            service.SetState("TransportState", "NO_MEDIA_PRESENT");
            var next = (Task)typeof(NvaCasting).GetField("_nextTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service.Casting)!;
            await next.WaitAsync(TimeSpan.FromSeconds(3));
            Require(fixture.LastCid == "old", "old NVA playlist resumed after DLNA takeover");

            var payload = new JsonObject
            { ["ver"] = 2, ["content"] = new JsonObject { ["aid"] = "10", ["cid"] = "20", ["ct"] = 2, ["sk"] = "12" } };
            var link = "bilibili://video?nva_ext=" + Uri.EscapeDataString(payload.ToJsonString());
            await StandardSoapAsync(http, "SetAVTransportURI", "<CurrentURI>" + System.Security.SecurityElement.Escape(link) + "</CurrentURI><CurrentURIMetaData/>");
            Require(fixture.LastCid == "20" && service.GetState("CurrentTrackURI") == "https://example.invalid/video.mp4", "SOAP Bilibili link was not resolved");
            await StandardSoapAsync(http, "Pause", "");
            Require(service.GetState("TransportState") == "PAUSED_PLAYBACK", "DLNA controls cannot control NVA item");
            await client.HandleCommandAsync("Resume", 3, null, Token);
            Require(service.GetState("TransportState") == "PLAYING", "NVA resume failed after DLNA pause");

            await StandardSoapAsync(http, "SetAVTransportURI", "<CurrentURI>https://example.invalid/second.mp4</CurrentURI><CurrentURIMetaData/>");
            await client.HandleCommandAsync("Play", 4, Play("30"), Token);
            Require(client.DescribePlay()["cid"]!.GetValue<string>() == "30", "NVA could not take over DLNA");
            Require(service.Port == port && ReferenceEquals(service.Renderer, renderer), "takeover restarted listener or renderer");
            Require((await http.GetStringAsync("/health")).Contains("\"protocol\":\"Auto\"", StringComparison.Ordinal), "automatic health endpoint missing");
        }, failures);

        await CheckAsync("Bilibili SOAP playback works without NVA sockets", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            using var renderer = new MpvRenderer(new RendererContext(service.Settings, service, true));
            service.Renderer = renderer;
            renderer.Start();
            var payload = new JsonObject
            { ["ver"] = 2, ["content"] = new JsonObject { ["aid"] = "10", ["cid"] = "20", ["ct"] = 2 } };
            var link = "bilibili://video?nva_ext=" + Uri.EscapeDataString(payload.ToJsonString());
            await service.CallSoapAsync(StandardSoap("SetAVTransportURI", "<CurrentURI>" + System.Security.SecurityElement.Escape(link) + "</CurrentURI><CurrentURIMetaData/>"), Token);
            Require(service.Casting.ClientCount == 0 && fixture.LastCid == "20", "SOAP playback required a control socket");
            Require(await service.Casting.ApplyQualityAsync(80, Token), "SOAP-originated item could not switch quality");
            Require(service.GetState("TransportState") == "PLAYING", "SOAP-originated playback stopped");
        }, failures);

        await CheckAsync("cross-protocol playback commands are serialized", async () =>
        {
            using var fixture = new Fixture { DelayPlayUrl = true };
            await using var service = fixture.CreateService();
            using var renderer = new MpvRenderer(new RendererContext(service.Settings, service, true));
            service.Renderer = renderer;
            renderer.Start();
            var client = Attach(service.Casting, "race");
            var nva = client.HandleCommandAsync("Play", 1, Play("blocked"), Token);
            await fixture.PlayUrlStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var body = StandardSoap("SetAVTransportURI", "<CurrentURI>https://example.invalid/last.mp4</CurrentURI><CurrentURIMetaData/>");
            var dlna = service.CallSoapAsync(body, Token);
            Require(!dlna.IsCompleted, "DLNA bypassed shared command lock");
            fixture.ContinuePlayUrl.TrySetResult();
            await Task.WhenAll(nva, dlna).WaitAsync(TimeSpan.FromSeconds(3));
            Require(service.GetState("CurrentTrackURI") == "https://example.invalid/last.mp4", "earlier NVA request overwrote newer DLNA item");
        }, failures);

        await CheckAsync("NVA saved login and expiry", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            await service.Actions.CallAsync(Soap("LoginWithCode", "<Code>test-code</Code>"), Token);
            Require(service.Settings.Get<long>("NVA_AccessKeyExpiresAt", 0) > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "login expiry not stored");
            var client = Attach(service.Casting, "login");
            await client.HandleCommandAsync("Play", 1, Play("400"), Token);
            Require(fixture.LastKey == "fake-login", "saved login was not used for playback");
            var expired = await service.Actions.CallAsync(Soap("GetAppInfo"), Token);
            Require(Encoding.UTF8.GetString(expired).Contains("<CurrentSignedIn>1</CurrentSignedIn>"), "valid login not reported");
            service.Settings.Set("NVA_AccessKeyExpiresAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
            expired = await service.Actions.CallAsync(Soap("GetAppInfo"), Token);
            Require(Encoding.UTF8.GetString(expired).Contains("<CurrentSignedIn>0</CurrentSignedIn>"), "expired login still active");
            await client.HandleCommandAsync("Play", 2, Play("401"), Token);
            Require(fixture.LastKey.Length == 0, "expired key used for playback");
            var explicitKey = Play("402");
            explicitKey["accessKey"] = "fake-phone";
            await client.HandleCommandAsync("Play", 3, explicitKey, Token);
            Require(fixture.LastKey == "fake-phone", "phone key should take priority");
        }, failures);

        await CheckAsync("NVA playlist types and multi-segment playback", async () =>
        {
            using var fixture = new Fixture { MultipleSegments = true };
            var api = new BilibiliApi(fixture.Http);
            var normal = await api.GetVideoInfoAsync("1", 0, 0, "20", Token);
            Require(normal!.PlayList[0].Epid == "0" && normal.PlayList[0].Oid == "10", "normal item treated as bangumi");
            var bangumi = await api.GetVideoInfoAsync("1", 5, 10, "20", Token);
            Require(bangumi!.PlayList[0].Epid == "10", "bangumi episode lost");
            var result = await api.GetPlayUrlAsync(new NvaPlayUrlRequest { Cid = "20" }, Token);
            Require(result?.Segments?.Count == 2 && result.Segments[1].DurationSeconds == 2, "durl segments or durations discarded");
            await using var service = fixture.CreateService();
            using var renderer = new MpvRenderer(new RendererContext(service.Settings, service, true));
            service.Renderer = renderer;
            renderer.Start();
            var client = Attach(service.Casting, "segments");
            await client.HandleCommandAsync("Play", 1, Play("20"), Token);
            Require(service.GetState("TransportState") == "PLAYING", "segmented renderer not used");
        }, failures);

        await CheckAsync("NVA old background work is cancelled", async () =>
        {
            using var fixture = new Fixture { DelayOldMetadata = true };
            await using var service = fixture.CreateService();
            var client = Attach(service.Casting, "background");
            var oldPlay = Play("old");
            oldPlay["aid"] = "old";
            oldPlay["contentType"] = 1;
            await client.HandleCommandAsync("Play", 1, oldPlay, Token);
            await fixture.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var newPlay = Play("new");
            newPlay["aid"] = "new";
            newPlay["contentType"] = 1;
            await client.HandleCommandAsync("Play", 2, newPlay, Token);
            await fixture.NewFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var extraTask = (Task)typeof(NvaCasting).GetField("_extraTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service.Casting)!;
            await extraTask.WaitAsync(TimeSpan.FromSeconds(3));
            await service.Casting.StopBackgroundAsync();
            Require(fixture.OldCancelled, "old metadata request kept running");
            Require(fixture.DanmakuRequests == 0, "background work still downloads danmaku");
            Require(!File.Exists(Path.Combine(AppSettings.ConfigDir, "macast.ass")), "playback generated danmaku subtitles");
        }, failures);
    }

    private static JsonObject Play(string cid) => new()
    { ["aid"] = "123", ["cid"] = cid, ["contentType"] = 2, ["userDesireQn"] = 80 };

    private static byte[] StandardSoap(string action, string arguments) => Encoding.UTF8.GetBytes(
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
        $"<u:{action} xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID>{arguments}</u:{action}></s:Body></s:Envelope>");

    private static async Task StandardSoapAsync(HttpClient http, string action, string arguments)
    {
        using var response = await http.PostAsync("/AVTransport/action", new ByteArrayContent(StandardSoap(action, arguments)));
        Require(response.IsSuccessStatusCode, "SOAP failed: " + await response.Content.ReadAsStringAsync());
    }

    private static byte[] Soap(string action, string arguments = "") => Encoding.UTF8.GetBytes(
        """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>""" +
        $"""<u:{action} xmlns:u="urn:app-bilibili-com:service:NirvanaControl:3">{arguments}</u:{action}>""" +
        "</s:Body></s:Envelope>");

    private static NvaClient Attach(NvaCasting casting, string session)
    {
        var method = typeof(NvaCasting).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (((NvaClient Client, bool Reused))method.Invoke(casting, new object[] { new MemoryStream(), session, "test-device" })!).Client;
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly HttpClient _http;
        public string LastCid = string.Empty;
        public string LastKey = string.Empty;
        public Dictionary<string, string> LastQuery = new();
        public HttpClient Http => _http;
        public bool MultipleSegments;
        public bool DashResponse;
        public bool DelayOldMetadata;
        public bool DelayPlayUrl;
        public TaskCompletionSource PlayUrlStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinuePlayUrl { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DanmakuRequests;
        public bool OldCancelled;
        public TaskCompletionSource OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NewFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture() => _http = new HttpClient(this, disposeHandler: false);
        public NvaProtocolService CreateService() => new(new AppSettings(), true, new BilibiliApi(_http));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/x/tv/playurl")
            {
                var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                LastQuery = query;
                LastCid = query["cid"];
                if (DelayPlayUrl && LastCid == "blocked")
                {
                    PlayUrlStarted.TrySetResult();
                    await ContinuePlayUrl.Task.WaitAsync(cancellationToken);
                }
                LastKey = query["access_key"];
                if (DashResponse && query.ContainsKey("fnval"))
                    return Json("""{"code":0,"data":{"quality":80,"dash":{"video":[{"id":80,"base_url":"https://example.invalid/video.m4s"}],"audio":[{"bandwidth":192000,"base_url":"https://example.invalid/audio.m4s"}]}}}""");
                if (MultipleSegments)
                    return Json("""{"code":0,"data":{"quality":80,"durl":[{"url":"https://example.invalid/1.flv","length":1000},{"url":"https://example.invalid/2.flv","length":2000}]}}""");
                return Json("""{"code":0,"data":{"quality":80,"durl":[{"url":"https://example.invalid/video.mp4"}]}}""");
            }
            if (uri.AbsolutePath == "/x/tv/card/view_v2")
            {
                if (DelayOldMetadata && uri.Query.Contains("object_id=old", StringComparison.Ordinal))
                {
                    OldStarted.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { OldCancelled = true; throw; }
                }
                if (uri.Query.Contains("object_id=new", StringComparison.Ordinal)) NewFinished.TrySetResult();
                return Json("""{"code":0,"data":{"title":"series","auto_play":{"cid_list":[{"aid":"10","playurl_args":{"object_id":"10","cid":"20"},"title":"part"}]}}}""");
            }
            if (uri.Host == "comment.bilibili.com")
            {
                DanmakuRequests++;
                throw new InvalidOperationException("danmaku must not be requested");
            }
            if (uri.AbsolutePath.EndsWith("/qrcode/poll"))
                return Json("""{"code":0,"data":{"access_token":"fake-login","mid":"1","expires_in":3600}}""");
            return Json("""{"code":-1,"message":"not used by this test"}""");
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };

        protected override void Dispose(bool disposing)
        {
            if (disposing) _http.Dispose();
            base.Dispose(disposing);
        }
    }
}
