using Macast.Core.Renderer;
using Macast.Core.Settings;

namespace Macast.Core.Plugins;

public sealed class ProtocolContext
{
    public ProtocolContext(AppSettings settings, bool loopbackOnly = false)
    {
        Settings = settings;
        LoopbackOnly = loopbackOnly;
    }

    public AppSettings Settings { get; }

    public bool LoopbackOnly { get; }
}

/// <summary>
/// A live protocol instance: it owns the HTTP listener (DLNA on Kestrel, NVA on
/// its own socket server) and publishes media state to the renderer.
/// </summary>
public interface IProtocolService : IProtocolStateSink, IAsyncDisposable
{
    string Id { get; }

    /// <summary>Device description served at /description.xml.</summary>
    string Description { get; }

    int Port { get; }

    /// <summary>The renderer whose state this protocol publishes.</summary>
    IRenderer? Renderer { get; set; }

    Task StartAsync(int preferredPort, CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>Rebuilds /description.xml, e.g. after the friendly name changed.</summary>
    void RefreshDescription();
}

public interface IProtocolPlugin
{
    MacastPlugin Metadata { get; }

    IProtocolService Create(ProtocolContext context);
}
