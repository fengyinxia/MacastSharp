namespace Macast.Core.Utils;

public static class AssetPaths
{
    public static string BaseDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string XmlDir => Path.Combine(BaseDir, "xml");

    public static string IconPath => Path.Combine(BaseDir, "icon.ico");

    public static string ProtocolInfo => Path.Combine(XmlDir, "SinkProtocolInfo.csv");

    public static string Version
    {
        get
        {
            try
            {
                var path = Path.Combine(BaseDir, "version.txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : "0.7";
            }
            catch
            {
                return "0.7";
            }
        }
    }
}
