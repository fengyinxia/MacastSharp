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
        public override string Id => "NVA";

        public override string Title => "NVA Protocol";

        public override string Description => "NVA(nirvana) 投屏协议，B 站「哔哩必连 / 云视听小电视」，支持弹幕、倍速与自动连播";

        public override string Author => "xfangfang";

        public override string Version => "0.33";

        public override PluginKind Kind => PluginKind.Protocol;

        public override IReadOnlyList<string> Platforms { get; } =
            new[] { PlatformInfo.Windows, PlatformInfo.MacOS, PlatformInfo.Linux };
    }
}
