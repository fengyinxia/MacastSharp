using Macast.Core.Renderer;
using Macast.Core.Settings;

namespace Macast.Core.Plugins.Builtin;

public sealed class PotPlayerPlugin : IRendererPlugin
{
    public MacastPlugin Metadata { get; } = new Info();

    public bool IsUsable(AppSettings settings) => PotPlayerRenderer.ResolveExecutable(settings) is not null;

    public IRenderer Create(RendererContext context) => new PotPlayerRenderer(context);

    private sealed class Info : MacastPlugin
    {
        public override string Id => "PotPlayer";

        public override string Title => "PotPlayer Renderer";

        public override string Description => "用 PotPlayer 独立窗口播放投屏内容，支持 DASH 分离音视频、窗口布局、精确音量和播放位置消息控制";

        public override string Author => "xfangfang";

        public override string Version => "0.4";

        public override PluginKind Kind => PluginKind.Renderer;

        public override IReadOnlyList<string> Platforms => new[] { PlatformInfo.Windows };

        public override IReadOnlyList<PluginSettingDescriptor> Settings => new[]
        {
            PluginSettingDescriptor.Path("Potplayer_Path", "PotPlayerMini64.exe 路径",
                @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe",
                @"留空或路径不存在时会读取注册表 HKCU\Software\DAUM\PotPlayer* 和默认安装目录。")
        };

        public override string StatusText(AppSettings settings) =>
            !Supported ? "当前平台不可用（仅 Windows）" :
            PotPlayerRenderer.ResolveExecutable(settings) is not null ? "已找到 PotPlayer" : "未找到 PotPlayer，请在下方设置路径";
    }
}
