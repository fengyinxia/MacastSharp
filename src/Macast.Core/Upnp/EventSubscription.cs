using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Macast.Core.Upnp;

public sealed class EventSubscription
{
    public string Sid { get; } = "uuid:" + Guid.NewGuid();
    public string Service { get; }
    public string Callback { get; }
    public int TimeoutSeconds { get; private set; }
    public DateTime StartedAt { get; private set; } = DateTime.UtcNow;
    public int Seq { get; set; }
    public int ErrorCount { get; set; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public bool IsExpired => (DateTime.UtcNow - StartedAt).TotalSeconds > TimeoutSeconds;

    public EventSubscription(string service, string callback, int timeoutSeconds)
    {
        Service = service;
        Callback = callback;
        TimeoutSeconds = timeoutSeconds;
    }

    public void Renew(int timeoutSeconds)
    {
        TimeoutSeconds = timeoutSeconds;
        StartedAt = DateTime.UtcNow;
    }
}

public static class EventXml
{
    private static readonly XNamespace EventNs = "urn:schemas-upnp-org:event-1-0";
    private static readonly XNamespace AvtNs = "urn:schemas-upnp-org:metadata-1-0/AVT/";
    private static readonly XNamespace RcsNs = "urn:schemas-upnp-org:metadata-1-0/RCS/";

    public static string Build(string service, IReadOnlyDictionary<string, string> changes)
    {
        var root = new XElement(EventNs + "propertyset",
            new XAttribute(XNamespace.Xmlns + "e", EventNs));

        if (string.Equals(service, "ConnectionManager", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in changes)
            {
                root.Add(new XElement(EventNs + "property",
                    new XElement(pair.Key, pair.Value)));
            }

            return root.ToString(SaveOptions.DisableFormatting);
        }

        var metadataNs = string.Equals(service, "RenderingControl", StringComparison.OrdinalIgnoreCase)
            ? RcsNs
            : AvtNs;

        var instance = new XElement(metadataNs + "InstanceID",
            new XAttribute("val", "0"));

        foreach (var pair in changes)
        {
            instance.Add(new XElement(metadataNs + pair.Key,
                new XAttribute("val", pair.Value)));
        }

        var eventElement = new XElement(metadataNs + "Event", instance);
        root.Add(new XElement(EventNs + "property",
            new XElement("LastChange", eventElement.ToString(SaveOptions.DisableFormatting))));

        return root.ToString(SaveOptions.DisableFormatting);
    }
}
