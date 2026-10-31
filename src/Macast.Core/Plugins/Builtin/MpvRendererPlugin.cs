using Macast.Core.Renderer;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Plugins.Builtin;

/// <summary>Built-in MPV renderer: the default Macast player.</summary>
public sealed class MpvRendererPlugin : IRendererPlugin
{
    public MacastPlugin Metadata { get; } = new Info();

    public bool IsUsable(AppSettings settings) => ResolveMpv(settings) is not null;

    public IRenderer Create(RendererContext context) => new MpvRenderer(context);

    public static string? ResolveMpv(AppSettings settings)
    {
        var exe = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
        return ExecutableLocator.Resolve(settings.MpvDefaultPath, exe)
               ?? ExecutableLocator.Resolve("mpv", exe);
    }

    private sealed class Info : MacastPlugin
    {
        public override string Id => "MPV";

        public override string Title => "MPV";

        public override string Description => "内置播放器，通过 mpv IPC 精确同步播放状态，支持进度、音量、字幕、倍速";

        public override string Author => "xfangfang";

        public override string Version => "0.7";

        public override PluginKind Kind => PluginKind.Renderer;
    }
}
