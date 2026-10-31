using System.Globalization;

namespace Macast.Core.Utils;

public static class MediaTime
{
    /// <summary>UPnP expects H:MM:SS; the renderers use the same format Python produced.</summary>
    public static string FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "00:00:00";
        }

        var total = (int)seconds;
        return $"{total / 3600}:{total % 3600 / 60:D2}:{total % 60:D2}";
    }

    /// <summary>Parses "123", "1:02:03" or "02:03" into seconds; 0 when unreadable.</summary>
    public static double ToSeconds(string? position)
    {
        if (string.IsNullOrWhiteSpace(position))
        {
            return 0;
        }

        var parts = position.Trim().Split(':');
        try
        {
            return parts.Length switch
            {
                1 => double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0,
                2 => int.Parse(parts[0]) * 60 + double.Parse(parts[1], CultureInfo.InvariantCulture),
                3 => int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + double.Parse(parts[2], CultureInfo.InvariantCulture),
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }
}
