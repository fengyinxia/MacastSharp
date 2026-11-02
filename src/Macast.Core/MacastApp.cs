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
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _disposed;
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
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StartCoreAsync(portOverride, cancellationToken);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(int? portOverride, CancellationToken cancellationToken)
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
                : NetworkUtils.GetAddresses();

            Ssdp.AdvertiseNva = Protocol is Protocols.Nva.NvaProtocolService;
            await Ssdp.StartAsync(actualPort, Addresses, cancellationToken);
            Renderer.Start();

            _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _monitorTask = MonitorNetworkAsync(_monitorCts.Token);
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
        await _lifecycle.WaitAsync(cancellationToken);
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
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
                await _monitorTask;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // normal shutdown
        }

        _monitorCts?.Dispose();
        _monitorCts = null;
        _monitorTask = null;
        await Ssdp.StopAsync(sendByeBye: true, CancellationToken.None);
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
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var wasRunning = Running;
            await StopCoreAsync();
            Renderer.Dispose();
            await Protocol.DisposeAsync();
            ProtocolPlugin = PluginRegistry.ResolveProtocol(Settings);
            Protocol = ProtocolPlugin.Create(new ProtocolContext(Settings, _loopbackOnly));
            RendererPlugin = PluginRegistry.ResolveRenderer(Settings);
            Renderer = CreateRenderer(RendererPlugin);
            Protocol.Renderer = Renderer;
            if (wasRunning) await StartCoreAsync(null, cancellationToken);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Swaps the renderer without touching the network (player window options).</summary>
    public async Task ReloadRendererAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Renderer.Dispose();
            Renderer = CreateRenderer(RendererPlugin);
            Protocol.Renderer = Renderer;
            if (Running) Renderer.Start();
            Log.Info($"renderer reloaded: {RendererPlugin.Metadata.Id}");
        }
        finally { _lifecycle.Release(); }
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
                    : NetworkUtils.GetAddresses();

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
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            await StopCoreAsync();
            Renderer.Dispose();
            await Protocol.DisposeAsync();
            await Ssdp.DisposeAsync();
            _disposed = true;
        }
        finally { _lifecycle.Release(); }
    }
}
