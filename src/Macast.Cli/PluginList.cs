using Macast.Core.Plugins;
using Macast.Core.Settings;

namespace Macast.Cli;

internal static class PluginList
{
    public static int Run()
    {
        var settings = AppSettings.Load();
        Console.WriteLine($"platform: {PlatformInfo.Current}   renderer: {settings.GetString("Macast_Renderer", PluginRegistry.DefaultRendererId)}" +
                          $"   protocol: {settings.GetString("Macast_Protocol", PluginRegistry.DefaultProtocolId)}");
        Console.WriteLine();

        Console.WriteLine("Renderers");
        foreach (var plugin in PluginRegistry.RendererPlugins)
        {
            var metadata = plugin.Metadata;
            Console.WriteLine($"  {metadata.Id,-10} {metadata.Title,-20} " +
                              $"{(plugin.IsUsable(settings) ? "ready" : "unavailable"),-12} {metadata.StatusText(settings)}");
            foreach (var setting in metadata.Settings)
            {
                Console.WriteLine($"      - {setting.Key} ({setting.Kind})");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Protocols");
        foreach (var plugin in PluginRegistry.ProtocolPlugins)
        {
            var metadata = plugin.Metadata;
            Console.WriteLine($"  {metadata.Id,-10} {metadata.Title,-20} {metadata.StatusText(settings)}");
        }

        return 0;
    }
}
