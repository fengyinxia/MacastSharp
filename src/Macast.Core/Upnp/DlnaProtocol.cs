using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using Macast.Core.Renderer;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Upnp;

public sealed class DlnaProtocol
{
    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";

    private static readonly Dictionary<string, string[]> ObservedStates = new(StringComparer.Ordinal)
    {
        ["AVTransport"] = new[]
        {
            "TransportState", "TransportStatus", "CurrentMediaDuration",
            "CurrentTrackDuration", "CurrentTrack", "NumberOfTracks"
        },
        ["RenderingControl"] = new[] { "Volume", "Mute" },
        ["ConnectionManager"] = new[]
        {
            "A_ARG_TYPE_Direction", "SinkProtocolInfo", "CurrentConnectionIDs"
        }
    };

    private readonly AppSettings _settings;
    private readonly Dictionary<string, ServiceDefinition> _services;
    private readonly Dictionary<string, StateVariable> _state = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EventSubscription> _subscriptions = new();
    private readonly Channel<(string Name, string Value)> _events =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _cts;

    public IRenderer? Renderer { get; set; }

    public IReadOnlyDictionary<string, ServiceDefinition> Services => _services;
    public string Version => _settings.Version;

    public DlnaProtocol(AppSettings settings)
    {
        _settings = settings;
        _services = ScpdLoader.Load();
        foreach (var service in _services.Values)
        {
            foreach (var variable in service.StateVariables.Values)
            {
                if (!_state.TryAdd(variable.Name, variable))
                {
                    Log.Debug($"duplicate state variable {variable.Name}, keeping first definition");
                }
            }
        }

        InitializeState();
    }

    private void InitializeState()
    {
        SetInitial("CurrentPlayMode", "NORMAL");
        SetInitial("TransportPlaySpeed", "1");
        SetInitial("TransportStatus", "OK");
        SetInitial("RelativeCounterPosition", "2147483647");
        SetInitial("AbsoluteCounterPosition", "2147483647");
        SetInitial("A_ARG_TYPE_Direction", "Output");
        SetInitial("CurrentConnectionIDs", "0");
        SetInitial("PlaybackStorageMedium", "None");

        try
        {
            if (File.Exists(AssetPaths.ProtocolInfo))
            {
                SetInitial("SinkProtocolInfo", File.ReadAllText(AssetPaths.ProtocolInfo).Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"read SinkProtocolInfo failed: {ex.Message}");
        }

        foreach (var variable in _state.Values)
        {
            if (string.IsNullOrEmpty(variable.Value) && variable.DefaultValue is not null)
            {
                variable.Value = variable.DefaultValue;
            }
        }
    }

    private void SetInitial(string name, string value)
    {
        if (_state.TryGetValue(name, out var variable))
        {
            variable.Value = value;
        }
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        while (_events.Reader.TryRead(out _))
        {
            // Drop events queued before the restart.
        }

        _ = Task.Run(() => EventLoopAsync(_cts.Token));
        _ = Task.Run(() => CleanupLoopAsync(_cts.Token));
        SetState("TransportState", "STOPPED");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public string GetState(string name) => _state.TryGetValue(name, out var variable) ? variable.Value : string.Empty;

    public void SetState(string name, string value)
    {
        if (!_state.TryGetValue(name, out var variable))
        {
            Log.Warn($"unknown state variable: {name}");
            return;
        }

        if (string.Equals(variable.Value, value, StringComparison.Ordinal))
        {
            return;
        }

        variable.Value = value;
        if (ObservedStates.Values.Any(list => list.Contains(name, StringComparer.Ordinal)))
        {
            _events.Writer.TryWrite((name, value));
        }
    }

    public void SetState(string name, bool value) => SetState(name, value ? "true" : "false");

    public void SetState(string name, int value) => SetState(name, value.ToString(CultureInfo.InvariantCulture));

    public byte[] Call(byte[] rawBody)
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
        var serviceParts = serviceNamespace.Split(':');
        if (serviceParts.Length < 4)
        {
            throw new InvalidDataException($"invalid service namespace: {serviceNamespace}");
        }

        var serviceName = serviceParts[3];
        if (!_services.TryGetValue(serviceName, out var service))
        {
            throw new InvalidDataException($"unknown service: {serviceName}");
        }

        if (!service.Actions.TryGetValue(actionName, out var action))
        {
            throw new InvalidDataException($"unknown action: {serviceName}/{actionName}");
        }

        var parameters = actionElement.Elements()
            .GroupBy(e => e.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

        foreach (var argument in action.Inputs)
        {
            if (parameters.TryGetValue(argument.Name, out var value))
            {
                SetState(argument.RelatedStateVariable, value);
            }
        }

        Dictionary<string, string> result;
        var method = GetType().GetMethod($"{serviceName}_{actionName}",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is not null)
        {
            try
            {
                result = (Dictionary<string, string>?)method.Invoke(this, new object[] { parameters })
                         ?? new Dictionary<string, string>();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
        else
        {
            result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var argument in action.Outputs)
            {
                result[argument.Name] = GetState(argument.RelatedStateVariable);
            }
        }

        return BuildSoapResponse(service.Namespace, actionName, result);
    }

    private static byte[] BuildSoapResponse(string serviceNamespace, string actionName,
        IReadOnlyDictionary<string, string> result)
    {
        XNamespace u = serviceNamespace;
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

    public static byte[] BuildSoapFault(int errorCode, string description)
    {
        XNamespace control = "urn:schemas-upnp-org:control-1-0";
        var fault = new XElement(SoapNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapNs),
            new XAttribute(SoapNs + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(SoapNs + "Body",
                new XElement(SoapNs + "Fault",
                    new XElement("faultcode", "s:Client"),
                    new XElement("faultstring", "UPnPError"),
                    new XElement("detail",
                        new XElement(control + "UPnPError",
                            new XElement("errorCode", errorCode),
                            new XElement("errorDescription", description))))));

        return Encoding.UTF8.GetBytes(fault.ToString(SaveOptions.DisableFormatting));
    }

    public (string Sid, string Timeout) Subscribe(string service, string callback, int timeoutSeconds)
    {
        if (!_services.ContainsKey(service))
        {
            throw new InvalidDataException($"unknown service: {service}");
        }

        var cleanCallback = callback.Trim().TrimStart('<').TrimEnd('>');
        if (!Uri.TryCreate(cleanCallback, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"invalid callback URL: {callback}");
        }

        foreach (var existing in _subscriptions.Values)
        {
            if (string.Equals(existing.Service, service, StringComparison.Ordinal) &&
                string.Equals(existing.Callback, cleanCallback, StringComparison.Ordinal))
            {
                existing.Renew(timeoutSeconds);
                return (existing.Sid, $"Second-{existing.TimeoutSeconds}");
            }
        }

        var subscription = new EventSubscription(service, cleanCallback, timeoutSeconds);
        _subscriptions[subscription.Sid] = subscription;
        _ = SendInitialEventAsync(subscription);
        return (subscription.Sid, $"Second-{subscription.TimeoutSeconds}");
    }

    public bool Renew(string sid, int timeoutSeconds)
    {
        if (_subscriptions.TryGetValue(sid, out var subscription))
        {
            subscription.Renew(timeoutSeconds);
            return true;
        }

        return false;
    }

    public bool Unsubscribe(string sid) => _subscriptions.TryRemove(sid, out _);

    private async Task SendInitialEventAsync(EventSubscription subscription)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ObservedStates.TryGetValue(subscription.Service, out var names))
        {
            foreach (var name in names)
            {
                data[name] = GetState(name);
            }
        }

        await SendEventSafeAsync(subscription, data, CancellationToken.None);
    }

    private async Task EventLoopAsync(CancellationToken token)
    {
        var buffer = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            while (await _events.Reader.WaitToReadAsync(token))
            {
                while (_events.Reader.TryRead(out var item))
                {
                    buffer[item.Name] = item.Value;
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                foreach (var group in buffer.GroupBy(pair =>
                             _state.TryGetValue(pair.Key, out var variable) ? variable.Service : string.Empty))
                {
                    if (string.IsNullOrEmpty(group.Key))
                    {
                        continue;
                    }

                    var data = group.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    foreach (var subscription in _subscriptions.Values
                                 .Where(s => string.Equals(s.Service, group.Key, StringComparison.Ordinal))
                                 .ToArray())
                    {
                        _ = SendEventSafeAsync(subscription, data, token);
                    }
                }

                buffer.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Log.Error($"event loop stopped: {ex.Message}");
        }
    }

    private async Task SendEventSafeAsync(EventSubscription subscription,
        IReadOnlyDictionary<string, string> data, CancellationToken token)
    {
        if (data.Count == 0)
        {
            return;
        }

        await subscription.SendLock.WaitAsync(token);
        try
        {
            if (subscription.IsExpired)
            {
                _subscriptions.TryRemove(subscription.Sid, out _);
                return;
            }

            var xml = EventXml.Build(subscription.Service, data);
            using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), subscription.Callback);
            request.Headers.TryAddWithoutValidation("NT", "upnp:event");
            request.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
            request.Headers.TryAddWithoutValidation("SERVER", ServerInfo());
            request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
            request.Headers.TryAddWithoutValidation("SEQ", subscription.Seq.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{subscription.TimeoutSeconds}");
            request.Content = new StringContent(xml, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"callback returned {(int)response.StatusCode}");
            }

            subscription.Seq++;
            subscription.ErrorCount = 0;
            Log.Debug($"event sent to {subscription.Callback} (SEQ={subscription.Seq - 1})");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            subscription.ErrorCount++;
            Log.Warn($"send event to {subscription.Callback} failed: {ex.Message}");
            if (subscription.ErrorCount > 10)
            {
                _subscriptions.TryRemove(subscription.Sid, out _);
            }
        }
        finally
        {
            subscription.SendLock.Release();
        }
    }

    private async Task CleanupLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                foreach (var subscription in _subscriptions.Values)
                {
                    if (subscription.IsExpired)
                    {
                        _subscriptions.TryRemove(subscription.Sid, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private string ServerInfo()
    {
        var os = Environment.OSVersion;
        return $"{os.Platform}/{os.Version} UPnP/1.0 Macast/{_settings.Version}";
    }

    // ---- DLNA actions -------------------------------------------------

    public Dictionary<string, string> AVTransport_SetAVTransportURI(Dictionary<string, string> data)
    {
        data.TryGetValue("CurrentURI", out var uri);
        uri ??= string.Empty;
        data.TryGetValue("CurrentURIMetaData", out var metadata);
        metadata ??= string.Empty;

        SetState("CurrentTrackURI", uri);
        SetState("CurrentTrackMetaData", metadata);
        Renderer?.SetMediaUrl(uri);

        var title = _settings.FriendlyName;
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            try
            {
                var document = XDocument.Parse(metadata);
                var titleElement = document.Descendants()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase));
                if (titleElement is not null && !string.IsNullOrWhiteSpace(titleElement.Value))
                {
                    title = titleElement.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"parse DIDL metadata failed: {ex.Message}");
            }
        }

        Renderer?.SetMediaTitle(title);
        Renderer?.SetMediaResume();
        SetState("CurrentTrackTitle", title);
        SetState("RelativeTimePosition", "00:00:00");
        SetState("AbsoluteTimePosition", "00:00:00");
        SetState("TransportState", "PAUSED_PLAYBACK");
        SetState("TransportStatus", "OK");
        return new Dictionary<string, string>();
    }

    public Dictionary<string, string> AVTransport_Play(Dictionary<string, string> data)
    {
        Renderer?.SetMediaResume();
        SetState("TransportState", "PLAYING");
        SetState("TransportStatus", "OK");
        return new Dictionary<string, string>();
    }

    public Dictionary<string, string> AVTransport_Pause(Dictionary<string, string> data)
    {
        Renderer?.SetMediaPause();
        SetState("TransportState", "PAUSED_PLAYBACK");
        return new Dictionary<string, string>();
    }

    public Dictionary<string, string> AVTransport_Stop(Dictionary<string, string> data)
    {
        Renderer?.SetMediaStop();
        SetState("TransportState", "STOPPED");
        return new Dictionary<string, string>();
    }

    public Dictionary<string, string> AVTransport_Seek(Dictionary<string, string> data)
    {
        if (data.TryGetValue("Target", out var target) && !string.IsNullOrWhiteSpace(target))
        {
            Renderer?.SetMediaPosition(target);
            SetState("RelativeTimePosition", target);
            SetState("AbsoluteTimePosition", target);
        }

        return new Dictionary<string, string>();
    }

    public Dictionary<string, string> AVTransport_SetPlayMode(Dictionary<string, string> data)
    {
        if (data.TryGetValue("NewPlayMode", out var mode) && !string.IsNullOrWhiteSpace(mode))
        {
            SetState("CurrentPlayMode", mode);
        }

        return new Dictionary<string, string>();
    }

    public Dictionary<string, string> RenderingControl_SetVolume(Dictionary<string, string> data)
    {
        if (data.TryGetValue("DesiredVolume", out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume))
        {
            Renderer?.SetMediaVolume(volume);
        }

        return new Dictionary<string, string>();
    }

    public Dictionary<string, string> RenderingControl_SetMute(Dictionary<string, string> data)
    {
        if (data.TryGetValue("DesiredMute", out var raw))
        {
            Renderer?.SetMediaMute(ParseBool(raw));
        }

        return new Dictionary<string, string>();
    }

    private static bool ParseBool(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false
        };
    }
}
