using Macast.Core.Plugins.Builtin;
using Macast.Core.Settings;

namespace Macast.Core.Plugins;

/// <summary>
/// Static catalogue of the plugins that ship with the C# port. Every plugin that
/// exists for the Python host has a C# implementation here; the ones that need a
/// different operating system stay visible in the settings window and report
/// themselves as unsupported instead of silently disappearing.
/// </summary>
public static class PluginRegistry
{
    public const string DefaultRendererId = "MPV";
    public const string DefaultProtocolId = "DLNA";

    private static readonly IReadOnlyList<IRendererPlugin> Renderers = new IRendererPlugin[]
    {
        new MpvRendererPlugin(),
        new PotPlayerPlugin(),
        new LiveRendererPlugin()
    };

    private static readonly IReadOnlyList<IProtocolPlugin> Protocols = new IProtocolPlugin[]
    {
        new DlnaProtocolPlugin(),
        new NvaProtocolPlugin()
    };

    public static IReadOnlyList<IRendererPlugin> RendererPlugins => Renderers;

    public static IReadOnlyList<IProtocolPlugin> ProtocolPlugins => Protocols;

    public static IRendererPlugin? FindRenderer(string? idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle))
        {
            return null;
        }

        var value = idOrTitle.Trim();
        return Renderers.FirstOrDefault(p => Matches(p.Metadata, value));
    }

    public static IProtocolPlugin? FindProtocol(string? idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle))
        {
            return null;
        }

        var value = idOrTitle.Trim();
        return Protocols.FirstOrDefault(p => Matches(p.Metadata, value));
    }

    /// <summary>Resolves Macast_Renderer; unknown ids fall back to MPV instead of failing to start.</summary>
    public static IRendererPlugin ResolveRenderer(AppSettings settings)
    {
        var configured = Environment.GetEnvironmentVariable("MACAST_RENDERER") is { Length: > 0 } overrideId
            ? overrideId
            : settings.GetString("Macast_Renderer", DefaultRendererId);
        var plugin = FindRenderer(configured);
        if (plugin is null)
        {
            Utils.Log.Warn($"unknown renderer '{configured}', falling back to {DefaultRendererId}");
            return Renderers[0];
        }

        return plugin;
    }

    /// <summary>Resolves Macast_Protocol; unknown ids fall back to DLNA.</summary>
    public static IProtocolPlugin ResolveProtocol(AppSettings settings)
    {
        var configured = Environment.GetEnvironmentVariable("MACAST_PROTOCOL") is { Length: > 0 } overrideId
            ? overrideId
            : settings.GetString("Macast_Protocol", DefaultProtocolId);
        var plugin = FindProtocol(configured);
        if (plugin is null)
        {
            Utils.Log.Warn($"unknown protocol '{configured}', falling back to {DefaultProtocolId}");
            return Protocols[0];
        }

        return plugin;
    }

    private static bool Matches(MacastPlugin metadata, string value) =>
        string.Equals(metadata.Id, value, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(metadata.Title, value, StringComparison.OrdinalIgnoreCase);
}
