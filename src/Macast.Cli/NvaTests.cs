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
        }, failures);

        await CheckAsync("NVA speed and danmaku survive quality change", async () =>
        {
            using var fixture = new Fixture();
            await using var service = fixture.CreateService();
            using var renderer = new MpvRenderer(new RendererContext(service.Settings, service, true));
            service.Renderer = renderer;
            renderer.Start();
            var client = Attach(service.Casting, "speed");
            var play = Play("300");
            play["danmakuSwitchSave"] = false;
            await client.HandleCommandAsync("Play", 1, play, Token);
            Require(service.GetState("DisplayCurrentSubtitle") == "false", "initial danmaku preference ignored");
            await client.HandleCommandAsync("SwitchSpeed", 2, new JsonObject { ["speed"] = 1.5 }, Token);
            await client.HandleCommandAsync("SwitchQn", 3, new JsonObject { ["qn"] = 80 }, Token);
            Require(service.GetState("TransportPlaySpeed") == "1.5", "quality switch reset speed");
            Require(service.GetState("DisplayCurrentSubtitle") == "false", "quality switch reset danmaku");
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
            using var fixture = new Fixture { DelayOldDanmaku = true };
            await using var service = fixture.CreateService();
            var client = Attach(service.Casting, "background");
            await client.HandleCommandAsync("Play", 1, Play("old"), Token);
            await fixture.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await client.HandleCommandAsync("Play", 2, Play("new"), Token);
            await fixture.NewFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var extraTask = (Task)typeof(NvaCasting).GetField("_extraTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service.Casting)!;
            await extraTask.WaitAsync(TimeSpan.FromSeconds(3));
            await service.Casting.StopBackgroundAsync();
            Require(fixture.OldCancelled, "old download kept running");
            Require(File.ReadAllText(Path.Combine(AppSettings.ConfigDir, "macast.ass")).Contains("new-comment"), "stale subtitle replaced new video");
            Require(!Directory.EnumerateFiles(AppSettings.ConfigDir, "macast.ass.*.tmp").Any(), "subtitle temporary file leaked");
        }, failures);
    }

    private static JsonObject Play(string cid) => new()
    { ["aid"] = "123", ["cid"] = cid, ["contentType"] = 2, ["userDesireQn"] = 80 };

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
        public bool DelayOldDanmaku;
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
                LastKey = query["access_key"];
                if (DashResponse && query.ContainsKey("fnval"))
                    return Json("""{"code":0,"data":{"quality":80,"dash":{"video":[{"id":80,"base_url":"https://example.invalid/video.m4s"}],"audio":[{"bandwidth":192000,"base_url":"https://example.invalid/audio.m4s"}]}}}""");
                if (MultipleSegments)
                    return Json("""{"code":0,"data":{"quality":80,"durl":[{"url":"https://example.invalid/1.flv","length":1000},{"url":"https://example.invalid/2.flv","length":2000}]}}""");
                return Json("""{"code":0,"data":{"quality":80,"durl":[{"url":"https://example.invalid/video.mp4"}]}}""");
            }
            if (uri.AbsolutePath == "/x/tv/card/view_v2")
                return Json("""{"code":0,"data":{"title":"series","auto_play":{"cid_list":[{"aid":"10","playurl_args":{"object_id":"10","cid":"20"},"title":"part"}]}}}""");
            if (uri.Host == "comment.bilibili.com")
            {
                if (DelayOldDanmaku && uri.AbsolutePath == "/old.xml")
                {
                    OldStarted.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { OldCancelled = true; throw; }
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""<i><d p="1,1,25,16777215,0,0,0,0">new-comment</d></i>""") };
                if (uri.AbsolutePath == "/new.xml") NewFinished.TrySetResult();
                return response;
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
