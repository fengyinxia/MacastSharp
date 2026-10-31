using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Macast.Core.Utils;

namespace Macast.Core.Settings;

public sealed class AppSettings
{
    public static string ConfigDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("MACAST_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Macast")
                : custom;
        }
    }

    public static string SettingPath => Path.Combine(ConfigDir, "macast_setting.json");

    private Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);

    public string Version { get; } = AssetPaths.Version;

    public string MachineFriendlyName { get; } = $"Macast({Environment.MachineName})";

    public string? TempFriendlyName { get; set; }

    public string MpvDefaultPath { get; set; } = "mpv";

    public IReadOnlyDictionary<string, JsonNode?> Values => _values;

    public string FriendlyName => TempFriendlyName ?? GetString("DLNA_FriendlyName", MachineFriendlyName);

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(SettingPath))
            {
                var json = File.ReadAllText(SettingPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json);
                if (dict is not null)
                {
                    settings._values = new Dictionary<string, JsonNode?>(dict, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"load settings failed: {ex.Message}");
        }

        return settings;
    }

    public string GetString(string key, string defaultValue = "")
    {
        if (_values.TryGetValue(key, out var node) && node is not null)
        {
            try
            {
                return node.GetValue<string>();
            }
            catch
            {
                try
                {
                    return node.ToString();
                }
                catch
                {
                    return defaultValue;
                }
            }
        }

        return defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0) =>
        _values.TryGetValue(key, out var node) && node is not null
            ? TryInt(node, defaultValue)
            : defaultValue;

    public bool GetBool(string key, bool defaultValue = false) =>
        _values.TryGetValue(key, out var node) && node is not null
            ? TryBool(node, defaultValue)
            : defaultValue;

    public List<string> GetStringList(string key)
    {
        var result = new List<string>();
        if (!_values.TryGetValue(key, out var node) || node is not JsonArray array)
        {
            return result;
        }

        foreach (var item in array)
        {
            if (item is not null)
            {
                try
                {
                    result.Add(item.GetValue<string>());
                }
                catch
                {
                    // ignore malformed entries
                }
            }
        }

        return result;
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (!_values.TryGetValue(key, out var node) || node is null)
        {
            return defaultValue;
        }

        try
        {
            return node.Deserialize<T>() ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        _values[key] = JsonSerializer.SerializeToNode(value);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            });
            File.WriteAllText(SettingPath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"save settings failed: {ex.Message}");
        }
    }

    public string GetUsn(bool refresh = false)
    {
        var usn = GetString("USN");
        if (refresh || string.IsNullOrWhiteSpace(usn))
        {
            usn = Guid.NewGuid().ToString();
            Set("USN", usn);
            Save();
        }

        return usn;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
    }

    public void ReplaceFromJson(string json)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json);
        if (dict is null)
        {
            throw new JsonException("settings must be a JSON object");
        }

        _values = new Dictionary<string, JsonNode?>(dict, StringComparer.Ordinal);
        Save();
    }

    private static int TryInt(JsonNode node, int fallback)
    {
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<int>(out var i))
                {
                    return i;
                }

                if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // fall through
        }

        return fallback;
    }

    private static bool TryBool(JsonNode node, bool fallback)
    {
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var b))
                {
                    return b;
                }

                if (value.TryGetValue<int>(out var i))
                {
                    return i != 0;
                }

                if (value.TryGetValue<string>(out var s))
                {
                    if (bool.TryParse(s, out var parsed))
                    {
                        return parsed;
                    }

                    if (int.TryParse(s, out var n))
                    {
                        return n != 0;
                    }
                }
            }
        }
        catch
        {
            // fall through
        }

        return fallback;
    }
}
