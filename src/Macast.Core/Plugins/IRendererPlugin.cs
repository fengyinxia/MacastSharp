using Macast.Core.Renderer;
using Macast.Core.Settings;

namespace Macast.Core.Plugins;

public sealed class RendererContext
{
    public RendererContext(AppSettings settings, IProtocolStateSink protocol, bool forceSimulation = false)
    {
        Settings = settings;
        Protocol = protocol;
        ForceSimulation = forceSimulation;
    }

    public AppSettings Settings { get; }

    public IProtocolStateSink Protocol { get; }

    /// <summary>True when the CLI was started with --no-mpv (protocol only).</summary>
    public bool ForceSimulation { get; }

    public string ConfigDir => AppSettings.ConfigDir;

    public string BundleBinDir => Path.Combine(AppContext.BaseDirectory, "bin");

    /// <summary>mpv / iina-cli / potplayer path resolved by the app for the current install.</summary>
    public string PlayerPath => Settings.MpvDefaultPath;
}

public interface IRendererPlugin
{
    MacastPlugin Metadata { get; }

    /// <summary>Checks the external program exists; false means the plugin is registered but cannot play.</summary>
    bool IsUsable(AppSettings settings);

    IRenderer Create(RendererContext context);
}
