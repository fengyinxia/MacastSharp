using Macast.Core.Utils;

namespace Macast.Tray;

/// <summary>Loads the tray icon from the Assets folder, falling back to the embedded copy.</summary>
internal static class TrayIcon
{
    private static Icon? _cached;

    public static Icon Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            if (File.Exists(AssetPaths.IconPath))
            {
                _cached = new Icon(AssetPaths.IconPath);
                return _cached;
            }
        }
        catch
        {
            // fall through to the embedded copy
        }

        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Macast.Tray.icon.ico");
            if (stream is not null)
            {
                _cached = new Icon(stream);
                return _cached;
            }
        }
        catch
        {
            // fall through
        }

        _cached = SystemIcons.Application;
        return _cached;
    }
}
