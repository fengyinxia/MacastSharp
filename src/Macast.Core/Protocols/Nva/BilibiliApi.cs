using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Macast.Core.Utils;
using Macast.Core.Renderer;

namespace Macast.Core.Protocols.Nva;

/// <summary>Arguments of one /x/tv/playurl call (nirvana.py: get_video_url).</summary>
public sealed record NvaPlayUrlRequest
{
    public string AccessKey { get; init; } = string.Empty;

    public string Cid { get; init; } = string.Empty;

    public string Oid { get; init; } = string.Empty;

    public string OgvAid { get; init; } = string.Empty;

    public int DesireQn { get; init; }

    /// <summary>1 = normal video, 2 = bangumi, 4 = live (nirvana.py playurl_type).</summary>
    public int PlayUrlType { get; init; } = 1;

    /// <summary>
    /// True for renderers with external audio support: request DASH (fnval=16) so HD is
    /// available, then attach the separate audio track. Otherwise durl is used.
    /// </summary>
    public bool PreferDash { get; init; }

    /// <summary>
    /// playurl client identity. The official TV app (NormalResolver) sends
    /// platform/mobi_app = android_tv_yst, build 106400. Using "ios" (what
    /// nirvana.py did) makes the server cap the result at 720P even when a valid
    /// access key is supplied.
    /// </summary>
    public string Platform { get; init; } = "android_tv_yst";

    public string Build { get; init; } = "106400";

    public string MobiApp { get; init; } = "android_tv_yst";
}

/// <summary>
/// playurl resolved to a playable URL plus the qn description sent to the client.
/// DASH responses carry the video and the audio as two URLs; AudioUrl is null for
/// durl/live streams where the audio is embedded.
/// </summary>
public sealed record NvaPlayUrlResult(string? Url, JsonObject Qn, int CurrentQn, string? AudioUrl = null,
    IReadOnlyList<MediaSegment>? Segments = null);

/// <summary>One entry of the auto play list (nirvana.py: card/view_v2 -&gt; cid_list).</summary>
public sealed record NvaPlayItem(string Aid, string Oid, string Cid, string Epid, string Title, bool IsPortrait);

/// <summary>card/view_v2 result: series title plus the flattened episode list.</summary>
public sealed record NvaVideoInfo(
    string Title,
    IReadOnlyList<NvaPlayItem> PlayList,
    int PlayIndex,
    bool IsPortrait,
    string CurrentEpisodeTitle);

/// <summary>TV login: the auth code / QR url, or the access key when the poll succeeded.</summary>
public sealed record NvaTvLogin(string AuthCode, string Url, string AccessKey, string Mid, int ExpiresIn);

/// <summary>Small helpers shared by the NVA classes when reading JsonNode payloads.</summary>
public static class NvaJson
{
    public static string GetString(JsonNode? node, string defaultValue = "")
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            try
            {
                return value.ToJsonString();
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        try
        {
            return node.ToJsonString();
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    public static int GetInt(JsonNode? node, int defaultValue = 0)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<long>(out var wide))
            {
                return (int)wide;
            }

            if (value.TryGetValue<double>(out var real))
            {
                return (int)real;
            }

            if (value.TryGetValue<string>(out var text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    public static double GetDouble(JsonNode? node, double defaultValue = 0)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var real))
            {
                return real;
            }

            if (value.TryGetValue<int>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    public static bool GetBool(JsonNode? node, bool defaultValue = false)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            if (value.TryGetValue<int>(out var number))
            {
                return number != 0;
            }

            if (value.TryGetValue<string>(out var text))
            {
                if (bool.TryParse(text, out var parsed))
                {
                    return parsed;
                }

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
                {
                    return numeric != 0;
                }

                return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return defaultValue;
    }

    public static bool ParseBool(string value, bool defaultValue = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var text = value.Trim();
        return text.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue
        };
    }
}

/// <summary>
/// Port of nirvana.py: <c>NetworkManager</c> (signed GET), <c>NVAConectionHandler.get_video_url</c>,
/// <c>get_video_info</c>.
///
/// The TV login helpers (QR code / poll / vip info) have no Python counterpart: the plugin only
/// implements the SOAP actions declared in NVA_SERVICE but leaves them unanswered, so those parts
/// are filled in from the public bilibili TV API and logged as a degradation.
/// </summary>
public sealed class BilibiliApi
{
    /// <summary>nirvana.py: APP_KEY (used by every signed request).</summary>
    public const string AppKey = "4ebafd7c4951b366";

    /// <summary>nirvana.py: APP_SEC.</summary>
    public const string AppSec = "8cb98205e9b2ad3669aad0fce12a4c13";

    private static readonly HttpClient DefaultClient = CreateClient(useProxy: true);
    private static readonly HttpClient DirectClient = CreateClient(useProxy: false);
    private readonly HttpClient _client;

    // The caller owns an injected client; production shares the standard client.
    public BilibiliApi(HttpClient? client = null) => _client = client ?? DefaultClient;

    private static HttpClient CreateClient(bool useProxy)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = useProxy,
            AllowAutoRedirect = true,
            // comment.bilibili.com answers "Content-Encoding: deflate" even when the client
            // did not ask for it, so decompression must be on for the danmaku xml to parse.
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Macast");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/client");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        return client;
    }

    // ---- nirvana.py NetworkManager ------------------------------------

    /// <summary>Signed URL: nirvana.py NetworkManager.GET(url, sign=True).</summary>
    public static string SignedUrl(string url)
    {
        var queryIndex = url.IndexOf('?');
        var path = queryIndex < 0 ? url : url[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : url[(queryIndex + 1)..];

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            parameters[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        parameters.Remove("sign");
        parameters["appkey"] = AppKey;
        parameters["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var basis = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var sign = Md5Hex(basis + AppSec);
        return $"{path}?{basis}&sign={sign}";
    }

    private static string Md5Hex(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string TimeStamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private async Task<string> GetStringAsync(string url, bool sign, CancellationToken cancellationToken)
    {
        var target = sign ? SignedUrl(url) : url;
        try
        {
            using var response = await _client.GetAsync(target, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // nirvana.py: fix proxy error with clash
            Log.Error($"nva requests.get {ex.GetType().Name}:{ex.Message}");
            if (ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var response = await DirectClient.GetAsync(target, cancellationToken);
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (Exception inner)
                {
                    Log.Error($"nva requests.get Exception:{inner.Message}");
                }
            }

            throw new InvalidOperationException("Cannot get any data from network.", ex);
        }
    }

    private async Task<JsonObject?> PostFormAsync(string url, IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(parameters);
        try
        {
            using var response = await _client.PostAsync(url, content, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error($"nva requests.post {ex.GetType().Name}:{ex.Message}");
            if (ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var direct = new FormUrlEncodedContent(parameters);
                    using var response = await DirectClient.PostAsync(url, direct, cancellationToken);
                    var text = await response.Content.ReadAsStringAsync(cancellationToken);
                    return JsonNode.Parse(text) as JsonObject;
                }
                catch (Exception inner)
                {
                    Log.Error($"nva requests.post Exception:{inner.Message}");
                }
            }

            throw new InvalidOperationException("Cannot get any data from network.", ex);
        }
    }

    private static JsonObject? ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException ex)
        {
            Log.Warn($"bilibili api returned invalid json: {ex.Message}");
            return null;
        }
    }

    // ---- nirvana.py NVAConectionHandler.get_video_url -----------------

    public async Task<NvaPlayUrlResult?> GetPlayUrlAsync(NvaPlayUrlRequest request, CancellationToken cancellationToken)
    {
        var parameters = new List<(string Key, string Value)>
        {
            ("access_key", request.AccessKey),
            ("actionKey", "appkey"),
            ("appkey", AppKey),
            ("build", request.Build),
            ("cid", request.Cid),
            ("fourk", "1"),
            ("is_proj", "1"),
            ("mobile_access_key", request.AccessKey),
            ("object_id", request.Oid),
            ("ogv_aid", request.OgvAid),
            ("platform", request.Platform),
            ("playurl_type", request.PlayUrlType.ToString(CultureInfo.InvariantCulture)),
            ("protocol", "0"),
            ("qn", request.DesireQn.ToString(CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrEmpty(request.MobiApp))
        {
            parameters.Add(("mobi_app", request.MobiApp));
        }

        if (request.PreferDash)
        {
            // The official TV app always asks for DASH and lets the player demux the
            // two tracks. durl (fnval absent) is capped at 1080P on the TV endpoint.
            parameters.Add(("fnval", "16"));
            parameters.Add(("fnver", "0"));
        }

        var url = "https://api.bilibili.com/x/tv/playurl?" +
                  string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        string text;
        try
        {
            text = await GetStringAsync(url, sign: true, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error($"error getting video urls {ex.Message}");
            return null;
        }

        var root = ParseObject(text);
        if (root is null)
        {
            return null;
        }

        var code = NvaJson.GetInt(root["code"], -1);
        if (code != 0)
        {
            var message = NvaJson.GetString(root["message"]);
            if (message.Length > 0)
            {
                Log.Warn($"bilibili playurl error: {message}");
            }

            return null;
        }

        if (root["data"] is not JsonObject data)
        {
            return null;
        }

        // qn_extras then support_formats, merged in insertion order (nirvana.py qn_support).
        var order = new List<int>();
        var entries = new Dictionary<int, JsonObject>();
        JsonObject Slot(int qn)
        {
            if (!entries.TryGetValue(qn, out var slot))
            {
                slot = new JsonObject();
                entries[qn] = slot;
                order.Add(qn);
            }

            return slot;
        }

        if (data["qn_extras"] is JsonArray qnExtras)
        {
            foreach (var item in qnExtras)
            {
                if (item is not JsonObject extra)
                {
                    continue;
                }

                var qn = NvaJson.GetInt(extra["qn"]);
                var slot = Slot(qn);
                slot["quality"] = qn;
                slot["needVip"] = NvaJson.GetBool(extra["need_vip"]);
                slot["needLogin"] = NvaJson.GetBool(extra["need_login"]);
            }
        }

        if (data["support_formats"] is JsonArray supportFormats)
        {
            foreach (var item in supportFormats)
            {
                if (item is not JsonObject format)
                {
                    continue;
                }

                var qn = NvaJson.GetInt(format["quality"]);
                var slot = Slot(qn);
                slot["description"] = NvaJson.GetString(format["new_description"]);
                slot["displayDesc"] = NvaJson.GetString(format["display_desc"]);
                slot["superscript"] = "Macast " + NvaJson.GetString(format["superscript"]);
            }
        }

        var list = new JsonArray();
        foreach (var qn in order)
        {
            list.Add(entries[qn].DeepClone());
        }

        var currentQn = NvaJson.GetInt(data["quality"]);
        var qnDescription = new JsonObject
        {
            ["curQn"] = currentQn,
            ["supportQnList"] = list,
            ["userDesireQn"] = request.DesireQn
        };

        if (data["durl"] is JsonArray durl && durl.Count > 0)
        {
            var segments = durl.OfType<JsonObject>().Select(item => new MediaSegment(
                NvaJson.GetString(item["url"]), NvaJson.GetDouble(item["length"]) / 1000)).ToArray();
            if (segments.Length != durl.Count || segments.Any(s => s.Url.Length == 0) ||
                (segments.Length > 1 && segments.Any(s => s.DurationSeconds <= 0)))
                return null;
            return new NvaPlayUrlResult(segments[0].Url, qnDescription, currentQn,
                Segments: segments.Length > 1 ? segments : null);
        }

        if (data["dash"] is JsonObject dash && dash["video"] is JsonArray videos && videos.Count > 0)
        {
            // Several qualities are returned at once; take the one the server granted
            // (data.quality) so the URL matches curQn, then the best audio track.
            JsonObject? video = null;
            foreach (var item in videos.OfType<JsonObject>())
            {
                video ??= item;
                if (NvaJson.GetInt(item["id"], -1) == currentQn)
                {
                    video = item;
                    break;
                }
            }

            var videoUrl = NvaJson.GetString(video?["base_url"], NvaJson.GetString(video?["baseUrl"]));
            string? audioUrl = null;
            if (dash["audio"] is JsonArray audios && audios.Count > 0)
            {
                var audio = audios.OfType<JsonObject>()
                    .OrderByDescending(a => NvaJson.GetInt(a["bandwidth"]))
                    .FirstOrDefault();
                audioUrl = NvaJson.GetString(audio?["base_url"], NvaJson.GetString(audio?["baseUrl"]));
            }

            if (videoUrl.Length > 0)
            {
                return new NvaPlayUrlResult(videoUrl, qnDescription, currentQn, audioUrl);
            }
        }

        if (data["live_mobile"] is JsonObject liveMobile)
        {
            var stream = NvaJson.GetString(liveMobile["stream"]);
            if (stream.Length > 0)
            {
                return new NvaPlayUrlResult(stream, qnDescription, currentQn);
            }
        }

        if (data["live_stream"] is JsonObject liveStream && liveStream["flv"] is JsonArray flv && flv.Count > 0)
        {
            return new NvaPlayUrlResult(NvaJson.GetString(flv[0]), qnDescription, currentQn);
        }

        Log.Warn("bilibili playurl returned no playable url (durl/dash/live_mobile/live_stream all empty)");
        return null;
    }

    // ---- nirvana.py NVAConectionHandler.get_video_info ----------------

    public async Task<NvaVideoInfo?> GetVideoInfoAsync(string oid, int seasonId, int epid, string cid,
        CancellationToken cancellationToken)
    {
        var parameters = new (string Key, string Value)[]
        {
            ("auto_play", "0"),
            ("build", "106400"),
            ("card_type", epid != 0 ? "2" : "1"),
            ("fourk", "0"),
            ("is_ad", "false"),
            ("mobi_app", "android_tv_yst"),
            ("object_id", epid != 0 ? seasonId.ToString(CultureInfo.InvariantCulture) : oid),
            ("view_type", "2")
        };

        var url = "https://api.bilibili.com/x/tv/card/view_v2?" +
                  string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"));

        string text;
        try
        {
            text = await GetStringAsync(url, sign: false, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error($"Error getting video info 2: {ex.Message}");
            return null;
        }

        var root = ParseObject(text);
        if (root is null)
        {
            return null;
        }

        var code = NvaJson.GetInt(root["code"], -1);
        if (code != 0)
        {
            var message = NvaJson.GetString(root["message"]);
            Log.Error($"Error getting video info 1: {message}");
            return null;
        }

        if (root["data"] is not JsonObject data)
        {
            return null;
        }

        var title = NvaJson.GetString(data["title"]);
        var playList = new List<NvaPlayItem>();
        var playIndex = 0;
        var isPortrait = false;
        var currentTitle = string.Empty;

        var cidList = data["auto_play"] is JsonObject autoPlay && autoPlay["cid_list"] is JsonArray list
            ? list
            : new JsonArray();

        foreach (var item in cidList)
        {
            if (item is not JsonObject video)
            {
                continue;
            }

            var titlePart = NvaJson.GetString(video["title"]);
            var longTitle = NvaJson.GetString(video["long_title"]);
            if (longTitle.Length > 0)
            {
                titlePart = longTitle;
            }

            var args = video["playurl_args"] as JsonObject;
            var objectId = NvaJson.GetString(args?["object_id"]);
            var episodeCid = NvaJson.GetString(args?["cid"]);
            var portrait = NvaJson.GetBool(video["is_portrait"]);

            playList.Add(new NvaPlayItem(
                NvaJson.GetString(video["aid"]),
                objectId,
                episodeCid,
                epid != 0 ? objectId : "0",
                titlePart,
                portrait));

            if (SameCid(cid, episodeCid))
            {
                playIndex = playList.Count - 1;
                isPortrait = portrait;
                currentTitle = titlePart;
                Log.Info($"当前正在播放 分集{playIndex + 1} {titlePart}");
            }
        }

        return new NvaVideoInfo(title, playList, playIndex, isPortrait, currentTitle);
    }

    private static bool SameCid(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) &&
               int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && a == b;
    }

    // ---- TV login / account (no python counterpart, see class comment) --

    /// <summary>Requests a TV login auth code; the client scans <c>Url</c> or hands the code back.</summary>
    public async Task<NvaTvLogin?> RequestTvLoginQrAsync(CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appkey"] = AppKey,
            ["local_tv_id"] = Random.Shared.Next(10000, 999999).ToString(CultureInfo.InvariantCulture),
            ["mobi_app"] = "android_tv_yst",
            ["platform"] = "android",
            ["ts"] = TimeStamp()
        };

        return await LoginRequestAsync("https://passport.bilibili.com/x/passport-tv-login/qrcode/auth_code",
            parameters, cancellationToken);
    }

    /// <summary>Polls the TV login state of an auth code and returns the access key once accepted.</summary>
    public async Task<NvaTvLogin?> PollTvLoginAsync(string authCode, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appkey"] = AppKey,
            ["auth_code"] = authCode,
            ["ts"] = TimeStamp()
        };

        return await LoginRequestAsync("https://passport.bilibili.com/x/passport-tv-login/qrcode/poll",
            parameters, cancellationToken);
    }

    private async Task<NvaTvLogin?> LoginRequestAsync(string url, Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        parameters["sign"] = SignForm(parameters);
        var response = await PostFormAsync(url, parameters, cancellationToken);
        if (response is null)
        {
            return null;
        }

        var code = NvaJson.GetInt(response["code"], -1);
        if (code != 0)
        {
            Log.Warn($"bilibili login returned {code}: {NvaJson.GetString(response["message"])}");
            return null;
        }

        if (response["data"] is not JsonObject data)
        {
            return null;
        }

        return new NvaTvLogin(
            NvaJson.GetString(data["auth_code"]),
            NvaJson.GetString(data["url"]),
            NvaJson.GetString(data["access_token"], NvaJson.GetString(data["token_info"]?["access_token"])),
            NvaJson.GetString(data["mid"]),
            NvaJson.GetInt(data["expires_in"], NvaJson.GetInt(data["token_info"]?["expires_in"])));
    }

    /// <summary>Signs a form body exactly like the app: sorted params + appsec md5.</summary>
    private static string SignForm(IReadOnlyDictionary<string, string> parameters)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            if (string.Equals(pair.Key, "sign", StringComparison.Ordinal))
            {
                continue;
            }

            sorted[pair.Key] = pair.Value;
        }

        var basis = string.Join("&", sorted.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return Md5Hex(basis + AppSec);
    }

    /// <summary>VipInfo of the SOAP GetAccountInfo action; 0 when not logged in or on failure.</summary>
    public async Task<int> GetVipInfoAsync(string accessKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            Log.Warn("GetAccountInfo: no access key yet, returning VipInfo=0");
            return 0;
        }

        var url = "https://api.bilibili.com/x/vip/info?" +
                  string.Join("&", new (string Key, string Value)[]
                  {
                      ("access_key", accessKey),
                      ("appkey", AppKey)
                  }.Select(p => $"{p.Key}={p.Value}"));

        try
        {
            var text = await GetStringAsync(url, sign: true, cancellationToken);
            var root = ParseObject(text);
            if (root is null || NvaJson.GetInt(root["code"], -1) != 0 || root["data"] is not JsonObject data)
            {
                return 0;
            }

            return NvaJson.GetInt(data["vip_status"], NvaJson.GetInt(data["status"]));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn($"GetAccountInfo failed: {ex.Message}");
            return 0;
        }
    }

    // ---- PlayUrl helper ------------------------------------------------

    /// <summary>nirvana.py PlayUrl: json.loads(parse.parse_qs(url)['nva_ext'][0]).</summary>
    public static JsonObject? ParseNvaExtension(string url)
    {
        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0)
        {
            return null;
        }

        foreach (var pair in url[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0 || !(pair[..equals].Equals("nva_ext", StringComparison.OrdinalIgnoreCase) ||
                                pair[..equals].Equals("_nva_ext_", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var raw = Uri.UnescapeDataString(pair[(equals + 1)..].Replace("+", "%20", StringComparison.Ordinal));
            try
            {
                return JsonNode.Parse(raw) as JsonObject;
            }
            catch (JsonException ex)
            {
                Log.Warn($"nva_ext is not valid json: {ex.Message}");
                return null;
            }
        }

        return null;
    }
}
