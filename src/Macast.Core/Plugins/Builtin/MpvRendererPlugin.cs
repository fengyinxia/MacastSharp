using Macast.Core.Renderer;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Plugins.Builtin;

/// <summary>MPV renderer using an externally installed player.</summary>
public sealed class MpvRendererPlugin : IRendererPlugin
{
    public MacastPlugin Metadata { get; } = new Info();

    public bool IsUsable(AppSettings settings) => ResolveMpv(settings) is not null;

    public IRenderer Create(RendererContext context) => new MpvRenderer(context);

    public static string? ResolveMpv(AppSettings settings)
    {
        var exe = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
        var configured = settings.GetString("Mpv_Path").Trim().Trim('"');
        if (configured.Length > 0)
        {
            var path = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
            return File.Exists(path) ? path :
                !configured.Contains('/') && !configured.Contains('\\') && !Path.IsPathRooted(configured)
                    ? ExecutableLocator.OnPath(configured) : null;
        }
        var cwd = Path.Combine(Environment.CurrentDirectory, "bin", exe);
        return ExecutableLocator.Resolve(settings.MpvDefaultPath, exe)
               ?? (File.Exists(cwd) ? cwd : null);
    }

    private sealed class Info : MacastPlugin
    {
        public override string Id => "MPV";

        public override string Title => "MPV";

        public override string Description => "使用自行安装的 MPV，通过 IPC 同步播放状态，支持进度、音量、字幕、倍速";

        public override string Author => "xfangfang";

        public override string Version => "0.7";

        public override PluginKind Kind => PluginKind.Renderer;

        public override IReadOnlyList<PluginSettingDescriptor> Settings => new[]
        {
            PluginSettingDescriptor.Path("Mpv_Path", "MPV 程序路径", "",
                "请选择自行安装的 mpv.exe；留空时自动查找程序目录 bin 和 PATH。")
        };

        public override string StatusText(AppSettings settings) => ResolveMpv(settings) is not null
            ? "已找到 MPV" : "未找到 MPV，请在下方设置程序路径";
    }
}
