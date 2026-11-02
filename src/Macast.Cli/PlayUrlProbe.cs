using Macast.Core.Protocols.Nva;

namespace Macast.Cli;

/// <summary>
/// Diagnostic: resolve a bilibili playurl exactly like the NVA receiver does.
/// Handy to check which qualities an account can actually get:
///
///   macast-cli --probe-playurl --aid 80433022 --cid 137649199 --qn 80
///   macast-cli --probe-playurl --aid ... --cid ... --qn 112 --access-key ...
///   macast-cli --probe-playurl ... --platform android_tv_yst --build 106400 --mobi-app android_tv_yst
///   macast-cli --probe-playurl ... --show-urls
///
/// The access key is the one the phone passes to the TV in nva_ext; without it
/// the TV endpoint caps the result at 720P.
/// </summary>
internal static class PlayUrlProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string Option(string name, string fallback = "")
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        var aid = Option("--aid");
        var cid = Option("--cid");
        var oid = Option("--oid", aid);
        var epid = Option("--ep-id", "0");
        var accessKey = Option("--access-key", Environment.GetEnvironmentVariable("BILI_ACCESS_KEY") ?? "");
        var qn = int.TryParse(Option("--qn", "80"), out var parsedQn) ? parsedQn : 80;
        var dash = !args.Contains("--no-dash");
        var defaults = new NvaPlayUrlRequest();
        var platform = Option("--platform", defaults.Platform);
        var build = Option("--build", defaults.Build);
        var mobiApp = Option("--mobi-app", defaults.MobiApp);
        var showUrls = args.Contains("--show-urls");

        if (aid.Length == 0 || cid.Length == 0)
        {
            Console.Error.WriteLine("usage: macast-cli --probe-playurl --aid AID --cid CID [--qn 80] [--access-key KEY] [--no-dash]");
            return 2;
        }

        var api = new BilibiliApi();
        var request = new NvaPlayUrlRequest
        {
            AccessKey = accessKey,
            Cid = cid,
            Oid = int.TryParse(epid, out var episodeId) && episodeId != 0 ? epid : oid,
            OgvAid = aid,
            DesireQn = qn,
            PlayUrlType = episodeId != 0 ? 2 : 1,
            PreferDash = dash,
            Platform = platform,
            Build = build,
            MobiApp = mobiApp
        };

        var result = await api.GetPlayUrlAsync(request, CancellationToken.None);
        if (result is null)
        {
            Console.Error.WriteLine("playurl failed (see the log for the bilibili error message)");
            return 1;
        }

        Console.WriteLine($"curQn        : {result.CurrentQn} (requested {qn})");
        Console.WriteLine($"dash audio   : {(result.AudioUrl is null ? "no (durl / live)" : "yes")}");
        Console.WriteLine($"video url    : {(showUrls ? result.Url : Trim(result.Url))}");
        if (result.AudioUrl is not null)
        {
            Console.WriteLine($"audio url    : {(showUrls ? result.AudioUrl : Trim(result.AudioUrl))}");
        }

        if (result.Qn["supportQnList"] is System.Text.Json.Nodes.JsonArray list)
        {
            Console.WriteLine("support qn   :");
            foreach (var item in list)
            {
                Console.WriteLine($"  qn={item?["quality"],-4} {item?["displayDesc"],-8} " +
                                  $"needLogin={item?["needLogin"],-5} needVip={item?["needVip"],-5} {item?["description"]}");
            }
        }

        return 0;
    }

    private static string Trim(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "(none)";
        }

        var index = url.IndexOf('?');
        return index > 0 ? url[..index] + "?…" : url;
    }
}
