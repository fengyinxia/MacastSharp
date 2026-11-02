using System.Net.Http;
using Macast.Core.Plugins;
using Macast.Core.Settings;
using Macast.Core.Utils;

namespace Macast.Core.Renderer;

/// <summary>
/// Port of the "Live Renderer" plugin: mpv playback plus an IPTV playlist that
/// the tray can start directly. The playlist is downloaded once and cached in
/// the config directory, exactly like the Python plugin cached macast_live.m3u8.
/// </summary>
public sealed class LiveRenderer : IRenderer, IChannelListProvider, IAudioTrackRenderer, ISegmentedRenderer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly MpvRenderer _inner;
    private readonly string _cachePath;
    private readonly string _playlistUrl;
    private readonly List<RendererChannel> _channels = new();

    public LiveRenderer(RendererContext context)
    {
        _inner = new MpvRenderer(context, new MpvLaunchProfile { Name = "Live" });
        _playlistUrl = context.Settings.GetString("Live_PlaylistUrl", "http://notag.cn/live/macast_live.m3u8");
        _cachePath = Path.Combine(context.ConfigDir, "macast_live.m3u8");
        LoadCache();
    }

    public string Name => _inner.Name;

    public bool Available => _inner.Available;

    public IReadOnlyList<RendererChannel> Channels => _channels;

    public void Start() => _inner.Start();

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();

    public void SetMediaUrl(string url, string start = "0") => _inner.SetMediaUrl(url, start);

    public void SetMediaStop() => _inner.SetMediaStop();

    public void SetMediaPause() => _inner.SetMediaPause();

    public void SetMediaResume() => _inner.SetMediaResume();

    public void SetMediaVolume(int volume) => _inner.SetMediaVolume(volume);

    public void SetMediaMute(bool mute) => _inner.SetMediaMute(mute);

    public void SetMediaTitle(string title) => _inner.SetMediaTitle(title);

    public void SetMediaUrlWithAudio(string url, string start, string audioUrl) =>
        _inner.SetMediaUrlWithAudio(url, start, audioUrl);

    public void SetMediaSegments(IReadOnlyList<MediaSegment> segments, string start) => _inner.SetMediaSegments(segments, start);

    public void SetMediaPosition(string position) => _inner.SetMediaPosition(position);

    public void SetMediaSpeed(double speed) => _inner.SetMediaSpeed(speed);

    public void SetMediaSubFile(string url, string title) => _inner.SetMediaSubFile(url, title);

    public void SetMediaSubShow(bool visible) => _inner.SetMediaSubShow(visible);

    public void SetMediaText(string text, int durationMs = 1000) => _inner.SetMediaText(text, durationMs);

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                Parse(File.ReadAllText(_cachePath));
                Log.Info($"live playlist loaded from cache: {_channels.Count} channels");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"read live playlist cache failed: {ex.Message}");
        }
    }

    private void Parse(string text)
    {
        _channels.Clear();
        string? pending = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                pending = comma >= 0 && comma + 1 < line.Length ? line[(comma + 1)..].Trim() : $"Channel {_channels.Count + 1}";
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            _channels.Add(new RendererChannel(pending ?? $"Channel {_channels.Count + 1}", line));
            pending = null;
        }
    }

    public async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_playlistUrl))
        {
            return _channels.Count;
        }

        try
        {
            var text = await Http.GetStringAsync(_playlistUrl, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllTextAsync(_cachePath, text, cancellationToken);
            Parse(text);
            Log.Info($"live playlist refreshed: {_channels.Count} channels");
        }
        catch (Exception ex)
        {
            Log.Warn($"download live playlist failed: {ex.Message}");
        }

        return _channels.Count;
    }

    public void Play(RendererChannel channel)
    {
        SetMediaTitle(channel.Name);
        SetMediaUrl(channel.Url);
    }
}
