using System.Net;
using Macast.Core.Net;
using Macast.Core.Plugins;
using Macast.Core.Renderer;
using Macast.Core.Settings;
using Macast.Core.Ssdp;
using Macast.Core.Utils;

namespace Macast.Core;

public sealed class MacastApp : IAsyncDisposable
{
    private readonly bool _loopbackOnly;
    private readonly bool _forceSimulation;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public AppSettings Settings { get; }

    public IProtocolService Protocol { get; private set; }

    public IProtocolPlugin ProtocolPlugin { get; private set; }

    public IRendererPlugin RendererPlugin { get; private set; }

    public IRenderer Renderer { get; private set; }

    public SsdpServer Ssdp { get; }

    public IReadOnlyList<InterfaceAddress> Addresses { get; private set; } = Array.Empty<InterfaceAddress>();

    public int Port => Protocol.Port;

    public bool Running { get; private set; }

    public string Description => Protocol.Description;

    public MacastApp(bool noMpv = false, bool loopbackOnly = false)
    {
        _loopbackOnly = loopbackOnly;
        _forceSimulation = noMpv;
        Settings = AppSettings.Load();
        Settings.MpvDefaultPath = ResolveMpvPath();
        Ssdp = new SsdpServer(Settings, loopbackOnly ? 1901 : SsdpServer.DefaultSsdpPort);

        ProtocolPlugin = PluginRegistry.ResolveProtocol(Settings);
        Protocol = ProtocolPlugin.Create(new ProtocolContext(Settings, loopbackOnly));
        RendererPlugin = PluginRegistry.ResolveRenderer(Settings);
        Renderer = CreateRenderer(RendererPlugin);
        Protocol.Renderer = Renderer;
    }

    private IRenderer CreateRenderer(IRendererPlugin plugin)
    {
        var context = new RendererContext(Settings, Protocol, _forceSimulation);
        try
        {
            var renderer = plugin.Create(context);
            if (!plugin.Metadata.Supported)
            {
                Log.Warn($"renderer '{plugin.Metadata.Title}' does not support {PlatformInfo.Current}; it will stay idle");
            }

            return renderer;
        }
        catch (Exception ex)
        {
            Log.Error($"create renderer '{plugin.Metadata.Title}' failed: {ex.Message}");
            var fallback = PluginRegistry.RendererPlugins[0];
            RendererPlugin = fallback;
            return fallback.Create(context);
        }
    }

    public async Task StartAsync(int? portOverride = null, CancellationToken cancellationToken = default)
    {
        if (Running)
        {
            return;
        }

        try
        {
            var preferredPort = portOverride ?? Settings.GetInt("ApplicationPort", 0);
            await Protocol.StartAsync(preferredPort, cancellationToken);

            var actualPort = Protocol.Port;
            var storedPort = Settings.GetInt("ApplicationPort", 0);
            if (actualPort != storedPort)
            {
                Settings.Set("USN", Guid.NewGuid().ToString());
                Settings.TempFriendlyName = $"Macast({Random.Shared.Next(0, 9999):D4})";
                Settings.Set("ApplicationPort", actualPort);
                Settings.Save();
                Protocol.RefreshDescription();
                Log.Info($"HTTP port changed from {storedPort} to {actualPort}; refreshed USN and friendly name");
            }

            Addresses = _loopbackOnly
                ? new List<InterfaceAddress>
                {
                    new("loopback", "loopback", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), false)
                }
                : NetworkUtils.GetAddresses(Settings);

            await Ssdp.StartAsync(actualPort, Addresses, cancellationToken);
            Renderer.Start();

            _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _monitorTask = Task.Run(() => MonitorNetworkAsync(_monitorCts.Token), _monitorCts.Token);
            Running = true;

            var ip = NetworkUtils.GetDefaultIp(Addresses);
            Log.Info($"Macast started: {Settings.FriendlyName} at http://{ip}:{Port} " +
                     $"renderer={RendererPlugin.Metadata.Id} protocol={ProtocolPlugin.Metadata.Id}");
        }
        catch
        {
            _monitorCts?.Cancel();
            try { await Protocol.StopAsync(); } catch { }
            try { Renderer.Stop(); } catch { }
            try { await Ssdp.StopAsync(false, CancellationToken.None); } catch { }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!Running)
        {
            return;
        }

        _monitorCts?.Cancel();
        try
        {
            if (_monitorTask is not null)
            {
                await _monitorTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // normal shutdown
        }

        await Ssdp.StopAsync(sendByeBye: true, cancellationToken);
        try
        {
            await Protocol.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"stop protocol failed: {ex.Message}");
        }

        Renderer.Stop();
        Running = false;
        Log.Info("Macast stopped");
    }

    /// <summary>
    /// Rebuilds the protocol and renderer from the current settings. The settings
    /// window calls this after changing the device name, port, network cards or
    /// the selected plugins.
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var wasRunning = Running;
        if (wasRunning)
        {
            await StopAsync(cancellationToken);
        }

        await Protocol.DisposeAsync();
        ProtocolPlugin = PluginRegistry.ResolveProtocol(Settings);
        Protocol = ProtocolPlugin.Create(new ProtocolContext(Settings, _loopbackOnly));
        RendererPlugin = PluginRegistry.ResolveRenderer(Settings);
        Renderer = CreateRenderer(RendererPlugin);
        Protocol.Renderer = Renderer;

        if (wasRunning)
        {
            await StartAsync(null, cancellationToken);
        }
    }

    /// <summary>Swaps the renderer without touching the network (player window options).</summary>
    public void ReloadRenderer()
    {
        try
        {
            Renderer.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"stop renderer failed: {ex.Message}");
        }

        Renderer = CreateRenderer(RendererPlugin);
        Protocol.Renderer = Renderer;
        if (Running)
        {
            Renderer.Start();
        }

        Log.Info($"renderer reloaded: {RendererPlugin.Metadata.Id}");
    }

    private async Task MonitorNetworkAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                if (!Running)
                {
                    continue;
                }

                var current = _loopbackOnly
                    ? new List<InterfaceAddress>
                    {
                        new("loopback", "loopback", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), false)
                    }
                    : NetworkUtils.GetAddresses(Settings);

                var before = Addresses.Select(a => $"{a.Address}/{a.Mask}").OrderBy(x => x).ToArray();
                var after = current.Select(a => $"{a.Address}/{a.Mask}").OrderBy(x => x).ToArray();
                if (!before.SequenceEqual(after))
                {
                    Log.Info($"network changed: {string.Join(",", before)} -> {string.Join(",", after)}");
                    Addresses = current;
                    await Ssdp.RestartAsync(Port, Addresses, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Log.Error($"network monitor stopped: {ex.Message}");
        }
    }

    private static string ResolveMpvPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "bin",
            OperatingSystem.IsWindows() ? "mpv.exe" : "mpv");
        return File.Exists(bundled) ? bundled : "mpv";
    }

    public string DeviceUrl()
    {
        var ip = NetworkUtils.GetDefaultIp(Addresses);
        return $"http://{ip}:{Port}";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Protocol.DisposeAsync();
        await Ssdp.DisposeAsync();
        try
        {
            Renderer.Stop();
        }
        catch
        {
            // ignore
        }
    }
}
