using Macast.Core.Protocols;

namespace Macast.Core.Plugins.Builtin;

public sealed class DlnaProtocolPlugin : IProtocolPlugin
{
    public MacastPlugin Metadata { get; } = new DlnaMetadata();

    public IProtocolService Create(ProtocolContext context) => new DlnaProtocolService(context.Settings);

    private sealed class DlnaMetadata : MacastPlugin
    {
        public override string Id => "DLNA";

        public override string Title => "DLNA";

        public override string Description => "标准 DLNA/UPnP 投屏协议（AVTransport + RenderingControl + ConnectionManager）";

        public override string Author => "xfangfang";

        public override string Version => "0.7";

        public override PluginKind Kind => PluginKind.Protocol;
    }
}
