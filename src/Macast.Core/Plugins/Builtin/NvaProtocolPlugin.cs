using Macast.Core.Protocols.Nva;

namespace Macast.Core.Plugins.Builtin;

/// <summary>
/// Port of the Macast-plugins "nirvana" plugin: the NVA protocol used by the bilibili
/// TV client (哔哩必连 / 云视听小电视). Metadata mirrors the python manifest.
/// </summary>
public sealed class NvaProtocolPlugin : IProtocolPlugin
{
    public MacastPlugin Metadata { get; } = new NvaMetadata();

    public IProtocolService Create(ProtocolContext context) => new NvaProtocolService(context.Settings, context.LoopbackOnly);

    private sealed class NvaMetadata : MacastPlugin
    {
        public override string Id => "Auto";

        public override string Title => "自动（DLNA + B站 NVA）";

        public override string Description => "DLNA 与 B站 NVA 同时接收，按请求自动识别，共用设备和播放器；支持高清、倍速与自动连播，不渲染弹幕。";

        public override string Author => "xfangfang";

        public override string Version => "0.33";

        public override PluginKind Kind => PluginKind.Protocol;

        public override IReadOnlyList<string> Platforms { get; } =
            new[] { PlatformInfo.Windows, PlatformInfo.MacOS, PlatformInfo.Linux };
    }
}
