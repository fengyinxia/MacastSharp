namespace Macast.Core.Plugins;

public enum PluginSettingKind
{
    Text = 0,
    Number = 1,
    Boolean = 2,
    Path = 3,
    Choice = 4
}

public sealed record PluginSettingChoice(string Value, string Label);

/// <summary>
/// Describes one configurable value of a plugin. The desktop settings window
/// renders these descriptors with plain WinForms controls and writes the value
/// back into macast_setting.json using <see cref="Key"/>.
/// </summary>
public sealed record PluginSettingDescriptor(
    string Key,
    string Label,
    PluginSettingKind Kind,
    object? DefaultValue = null,
    string? Help = null,
    IReadOnlyList<PluginSettingChoice>? Choices = null)
{
    public static PluginSettingDescriptor Text(string key, string label, string? defaultValue = null, string? help = null) =>
        new(key, label, PluginSettingKind.Text, defaultValue, help);

    public static PluginSettingDescriptor Number(string key, string label, int defaultValue = 0, string? help = null) =>
        new(key, label, PluginSettingKind.Number, defaultValue, help);

    public static PluginSettingDescriptor Boolean(string key, string label, bool defaultValue = false, string? help = null) =>
        new(key, label, PluginSettingKind.Boolean, defaultValue, help);

    public static PluginSettingDescriptor Path(string key, string label, string? defaultValue = null, string? help = null) =>
        new(key, label, PluginSettingKind.Path, defaultValue, help);

    public static PluginSettingDescriptor Choice(string key, string label, string defaultValue,
        IReadOnlyList<PluginSettingChoice> choices, string? help = null) =>
        new(key, label, PluginSettingKind.Choice, defaultValue, help, choices);
}
