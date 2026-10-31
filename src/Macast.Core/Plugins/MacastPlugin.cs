using Macast.Core.Settings;

namespace Macast.Core.Plugins;

public enum PluginKind
{
    Renderer = 0,
    Protocol = 1
}

/// <summary>
/// Metadata shared by every Macast plugin. The ids are the values stored in
/// Macast_Renderer / Macast_Protocol, so they must stay stable.
/// </summary>
public abstract class MacastPlugin
{
    public abstract string Id { get; }

    public abstract string Title { get; }

    public virtual string Description => string.Empty;

    public virtual string Author => "xfangfang";

    public virtual string Version => "0.1";

    public abstract PluginKind Kind { get; }

    public virtual IReadOnlyList<string> Platforms => PlatformInfo.All;

    /// <summary>Platform check only; a plugin can still be missing its external program.</summary>
    public bool Supported => PlatformInfo.Supports(Platforms);

    public virtual IReadOnlyList<PluginSettingDescriptor> Settings => Array.Empty<PluginSettingDescriptor>();

    /// <summary>Shown in the settings window next to the plugin name.</summary>
    public virtual string StatusText(AppSettings settings) =>
        Supported ? "可用" : $"当前平台不可用（仅 {string.Join(" / ", Platforms)}）";

    public override string ToString() => $"{Title} ({Id})";
}
