using Macast.Core.Plugins;
using Macast.Core.Renderer;
using Macast.Core.Server;
using Macast.Core.Settings;
using Macast.Core.Upnp;
using Macast.Core.Utils;

namespace Macast.Core.Protocols;

/// <summary>
/// The stock DLNA implementation: <see cref="DlnaProtocol"/> keeps the state and
/// answers SOAP, <see cref="MacastHttpServer"/> serves it over the shared socket HTTP transport.
/// </summary>
public sealed class DlnaProtocolService : IProtocolService
{
    private readonly AppSettings _settings;
    private readonly MacastHttpServer _server;
    private string _description = string.Empty;

    public DlnaProtocolService(AppSettings settings, bool loopbackOnly = false)
    {
        _settings = settings;
        Protocol = new DlnaProtocol(settings);
        _server = new MacastHttpServer(settings, Protocol, () => _description, loopbackOnly);
    }

    public DlnaProtocol Protocol { get; }

    public string Id => "DLNA";

    public string Description => _description;

    public int Port => _server.Port;

    public IRenderer? Renderer
    {
        get => Protocol.Renderer;
        set => Protocol.Renderer = value;
    }

    public async Task StartAsync(int preferredPort, CancellationToken cancellationToken = default)
    {
        Protocol.Start();
        await _server.StartAsync(preferredPort, cancellationToken);
        RefreshDescription();
        Log.Info($"DLNA protocol ready on port {Port}");
    }

    public void RefreshDescription()
    {
        _description = DeviceDescription.Build(_settings);
    }

    public async Task StopAsync()
    {
        await _server.StopAsync();
        await Protocol.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _server.DisposeAsync();
        await Protocol.DisposeAsync();
    }

    public string GetState(string name) => Protocol.GetState(name);

    public void SetState(string name, string value) => Protocol.SetState(name, value);

    public void SetState(string name, bool value) => Protocol.SetState(name, value);

    public void SetState(string name, int value) => Protocol.SetState(name, value);
}
