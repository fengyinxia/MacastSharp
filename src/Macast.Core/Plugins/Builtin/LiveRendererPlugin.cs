using Macast.Core.Renderer;
using Macast.Core.Settings;

namespace Macast.Core.Plugins.Builtin;

public sealed class LiveRendererPlugin : IRendererPlugin
{
    public MacastPlugin Metadata { get; } = new Info();

    public bool IsUsable(AppSettings settings) => MpvRendererPlugin.ResolveMpv(settings) is not null;

    public IRenderer Create(RendererContext context) => new LiveRenderer(context);

    private sealed class Info : MacastPlugin
    {
        public override string Id => "Live";

        public override string Title => "Live Renderer";

        public override string Description => "基于 mpv 的 IPTV 直播渲染器，托盘菜单可以直接切换频道";

        public override string Author => "dushan555";

        public override string Version => "0.2";

        public override PluginKind Kind => PluginKind.Renderer;

        public override IReadOnlyList<PluginSettingDescriptor> Settings => new[]
        {
            PluginSettingDescriptor.Text("Live_PlaylistUrl", "直播源 m3u8 地址",
                "http://notag.cn/live/macast_live.m3u8",
                "启动时从该地址拉取频道列表并缓存到配置目录；留空则只使用本地缓存。")
        };
    }
}
