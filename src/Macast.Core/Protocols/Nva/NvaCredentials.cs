using Macast.Core.Settings;

namespace Macast.Core.Protocols.Nva;

internal static class NvaCredentials
{
    public static string GetAccessKey(AppSettings settings)
    {
        var expiresAt = settings.Get<long>("NVA_AccessKeyExpiresAt", 0);
        if (expiresAt > 0 && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return string.Empty;
        return settings.GetString("NVA_AccessKey");
    }
}
