using Microsoft.Win32;

namespace Macast.Tray;

internal static class SettingsTheme
{
    public static Color Accent { get; private set; }
    public static Color Surface { get; private set; }
    public static Color Text { get; private set; }
    public static Color SecondaryText { get; private set; }

    public static void Initialize()
    {
        var dark = false;
        if (!SystemInformation.HighContrast)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        Apply(dark);
    }

    internal static void Apply(bool dark)
    {
        AntdUI.Config.Mode = dark ? AntdUI.TMode.Dark : AntdUI.TMode.Light;
        AntdUI.Config.Animation = SystemInformation.IsMenuAnimationEnabled;
        Accent = dark ? Color.FromArgb(77, 190, 166) : Color.FromArgb(20, 111, 96);
        Surface = dark ? Color.FromArgb(27, 31, 39) : Color.FromArgb(250, 251, 252);
        Text = dark ? Color.FromArgb(232, 237, 243) : Color.FromArgb(30, 40, 54);
        SecondaryText = dark ? Color.FromArgb(162, 174, 191) : Color.FromArgb(94, 108, 125);
        AntdUI.Style.SetPrimary(Accent);
        AntdUI.Style.Set(AntdUI.Colour.BgContainer, Surface);
        AntdUI.Style.Set(AntdUI.Colour.Text, Text);
        AntdUI.Style.Set(AntdUI.Colour.TextSecondary, SecondaryText);
    }
}
