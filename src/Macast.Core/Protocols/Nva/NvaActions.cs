using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Protocols.Nva;

/// <summary>
/// SOAP actions of the NirvanaControl service (urn:app-bilibili-com:service:NirvanaControl:3).
///
/// nirvana.py only registers the SCPD (NVA_SERVICE) and lets the generic DLNA dispatcher answer
/// with the declared defaults; the actions below are therefore implemented explicitly here, using
/// the same defaults plus the degradations documented on each method.
/// </summary>
public sealed class NvaActions
{
    public const string ServiceName = "NirvanaControl";

    public const string ServiceNamespace = "urn:app-bilibili-com:service:NirvanaControl:3";

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace ServiceNs = "urn:schemas-upnp-org:service-1-0";

    private readonly NvaProtocolService _service;
    private readonly Dictionary<string, string> _stateDefaults;

    public NvaActions(NvaProtocolService service)
    {
        _service = service;
        _stateDefaults = LoadStateDefaults();
    }

    public string SCPath => Path.Combine(AssetPaths.XmlDir, "NirvanaControl.xml");

    private static Dictionary<string, string> LoadStateDefaults()
    {
        // NVA_SERVICE defaults (NirvanaControl.xml), kept as a fallback when the file is missing.
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A_ARG_TYPE_UnlimitedString"] = string.Empty,
            ["A_ARG_TYPE_PackageName"] = "com.xiaodianshi.tv.yst",
            ["A_ARG_TYPE_AppKey"] = "0000000000000000",
            ["A_ARG_TYPE_Signature"] = "0000000000000000",
            ["A_ARG_TYPE_UnlimitedInt"] = string.Empty,
            ["A_ARG_TYPE_ScreenResolution"] = string.Empty,
            ["SignedIn"] = "1",
            ["DanmakuSwitch"] = string.Empty
        };

        var path = Path.Combine(AssetPaths.XmlDir, "NirvanaControl.xml");
        try
        {
            var document = XDocument.Load(path);
            foreach (var element in document.Descendants(ServiceNs + "stateVariable"))
            {
                var name = element.Element(ServiceNs + "name")?.Value.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                defaults[name] = element.Element(ServiceNs + "defaultValue")?.Value.Trim() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"NVA: cannot read {path}: {ex.Message}; using built-in defaults");
        }

        return defaults;
    }

    private string StateDefault(string name) =>
        _stateDefaults.TryGetValue(name, out var value) ? value : string.Empty;

    // ---- SOAP ----------------------------------------------------------

    public async Task<byte[]> CallAsync(byte[] rawBody, CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(rawBody), LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"invalid SOAP XML: {ex.Message}", ex);
        }

        var body = document.Root?.Element(SoapNs + "Body");
        var actionElement = body?.Elements().FirstOrDefault();
        if (actionElement is null)
        {
            throw new InvalidDataException("SOAP body has no action element");
        }

        var actionName = actionElement.Name.LocalName;
        var serviceNamespace = actionElement.Name.NamespaceName;
        if (!string.Equals(serviceNamespace, ServiceNamespace, StringComparison.Ordinal))
        {
            Log.Warn($"NVA: action {actionName} uses namespace '{serviceNamespace}' instead of '{ServiceNamespace}'");
        }

        var parameters = actionElement.Elements()
            .GroupBy(e => e.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

        var result = actionName switch
        {
            "GetAppInfo" => GetAppInfo(),
            "LoginWithCode" => await LoginWithCodeAsync(parameters, cancellationToken),
            "PrepareForMirrorProjection" => PrepareForMirrorProjection(),
            // Legacy actions remain callable, but danmaku rendering has been removed.
            "SetDanmakuSwitch" or "AppendDanmaku" => Empty(),
            "GetPlayInfo" => GetPlayInfo(),
            "GetDanmaku" => new Dictionary<string, string> { ["Content"] = string.Empty },
            "GetAccountInfo" => await GetAccountInfoAsync(cancellationToken),
            "SwitchQuality" => await SwitchQualityAsync(parameters, cancellationToken),
            _ => throw new InvalidDataException($"unknown action: {ServiceName}/{actionName}")
        };

        return BuildResponse(actionName, result);
    }

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private Dictionary<string, string> GetAppInfo() => new(StringComparer.Ordinal)
    {
        ["PackageName"] = StateDefault("A_ARG_TYPE_PackageName"),
        ["AppKey"] = StateDefault("A_ARG_TYPE_AppKey"),
        ["Signature"] = StateDefault("A_ARG_TYPE_Signature"),
        ["CurrentSignedIn"] = NvaCredentials.GetAccessKey(_service.Settings).Length > 0 ? "1" : "0"
    };

    private async Task<Dictionary<string, string>> LoginWithCodeAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var code = parameters.TryGetValue("Code", out var raw) ? raw.Trim() : string.Empty;
        if (code.Length == 0)
        {
            Log.Warn("NVA LoginWithCode: empty Code");
            return Empty();
        }

        try
        {
            // python 0.33只有 action 声明，没有实现；这里用 B 站 TV 登录 poll 兑换 access_key。
            var login = await _service.Bilibili.PollTvLoginAsync(code, cancellationToken);
            if (login is null || login.AccessKey.Length == 0)
            {
                Log.Warn("NVA LoginWithCode: code was not accepted (expired or already used)");
                return Empty();
            }

            _service.Settings.Set("NVA_AccessKey", login.AccessKey);
            _service.Settings.Set("NVA_AccessKeyExpiresAt", login.ExpiresIn > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + login.ExpiresIn : 0);
            _service.Settings.Save();
            Log.Info($"NVA LoginWithCode: signed in (mid {login.Mid}, expires in {login.ExpiresIn}s)");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error($"NVA LoginWithCode failed: {ex.Message}");
        }

        return Empty();
    }

    private Dictionary<string, string> PrepareForMirrorProjection()
    {
        // nirvana.py 声明了这个 action 但没有实现镜像投屏；返回占位尺寸，PushUrl 留空。
        Log.Warn("NVA PrepareForMirrorProjection: mirror projection is not implemented by nirvana.py 0.33");
        var width = _service.Settings.GetInt("NVA_ScreenWidth", 1920);
        var height = _service.Settings.GetInt("NVA_ScreenHeight", 1080);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ScreenWidth"] = width.ToString(CultureInfo.InvariantCulture),
            ["ScreenHeight"] = height.ToString(CultureInfo.InvariantCulture),
            ["PushUrl"] = string.Empty
        };
    }

    private Dictionary<string, string> GetPlayInfo()
    {
        // The phone deserialises this into LinkPlayInfoTotal (playState/position/
        // duration/danmakuState/title/volume/playItem/qn); anything else would be
        // dropped and the quality menu would show qn 0.
        var info = _service.Casting.DescribeActivePlayTotal();
        if (info.Count == 0)
        {
            Log.Debug("NVA GetPlayInfo: no active client, returning empty content");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content"] = info.ToJsonString()
        };
    }

    private async Task<Dictionary<string, string>> GetAccountInfoAsync(CancellationToken cancellationToken)
    {
        var accessKey = NvaCredentials.GetAccessKey(_service.Settings);
        var vipInfo = 0;
        try
        {
            vipInfo = await _service.Bilibili.GetVipInfoAsync(accessKey, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn($"NVA GetAccountInfo failed: {ex.Message}");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VipInfo"] = vipInfo.ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task<Dictionary<string, string>> SwitchQualityAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var qn = parameters.TryGetValue("Qn", out var raw) ? NvaCasting.ParseInt(raw, 0) : 0;
        // 等价于 NVA 命令 SwitchQn（python 版由客户端长连接发命令，SOAP 里没有实现）
        var applied = await _service.Casting.ApplyQualityAsync(qn, cancellationToken);
        if (!applied)
        {
            Log.Warn($"NVA SwitchQuality: qn {qn} was not applied (no client or no playable url)");
        }

        return Empty();
    }

    private static byte[] BuildResponse(string actionName, IReadOnlyDictionary<string, string> result)
    {
        XNamespace u = ServiceNamespace;
        var response = new XElement(u + $"{actionName}Response",
            new XAttribute(XNamespace.Xmlns + "u", u));

        foreach (var pair in result)
        {
            response.Add(new XElement(pair.Key, pair.Value));
        }

        var envelope = new XElement(SoapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapNs),
            new XAttribute(SoapNs + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(SoapNs + "Body", response));

        return Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
    }
}
