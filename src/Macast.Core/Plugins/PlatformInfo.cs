namespace Macast.Core.Plugins;

/// <summary>
/// The platform names used by Macast plugin metadata. They follow the values the
/// Python plugin manifest uses ("win32", "darwin", "linux") so that a plugin
/// written for either host maps to the same identifier.
/// </summary>
public static class PlatformInfo
{
    public const string Windows = "win32";
    public const string MacOS = "darwin";
    public const string Linux = "linux";

    public static readonly IReadOnlyList<string> All = new[] { Windows, MacOS, Linux };

    public static string Current =>
        OperatingSystem.IsWindows() ? Windows :
        OperatingSystem.IsMacOS() ? MacOS : Linux;

    public static bool Supports(IReadOnlyList<string>? platforms)
    {
        if (platforms is null || platforms.Count == 0)
        {
            return true;
        }

        return platforms.Any(p => string.Equals(p, Current, StringComparison.OrdinalIgnoreCase));
    }
}
