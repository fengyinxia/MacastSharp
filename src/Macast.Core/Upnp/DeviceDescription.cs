using System.Security;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Upnp;

/// <summary>Builds /description.xml from Assets/xml/Description.xml.</summary>
public static class DeviceDescription
{
    public static string Build(AppSettings settings, string headerExtra = "", string serviceExtra = "")
    {
        var templatePath = Path.Combine(AssetPaths.XmlDir, "Description.xml");
        var template = File.ReadAllText(templatePath);
        return template
            .Replace("{friendly_name}", Escape(settings.FriendlyName))
            .Replace("{manufacturer}", "xfangfang")
            .Replace("{manufacturer_url}", "https://github.com/xfangfang")
            .Replace("{model_description}", "AVTransport Media Renderer")
            .Replace("{model_name}", "Macast")
            .Replace("{model_url}", "https://xfangfang.github.io/Macast")
            .Replace("{model_number}", settings.Version)
            .Replace("{uuid}", settings.GetUsn())
            .Replace("{serial_num}", "1024")
            .Replace("{header_extra}", headerExtra)
            .Replace("{service_extra}", serviceExtra);
    }

    public static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
