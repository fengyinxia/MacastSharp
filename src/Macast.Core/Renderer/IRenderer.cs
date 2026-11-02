namespace Macast.Core.Renderer;

/// <summary>
/// A renderer that can attach a second, audio-only stream. Bilibili DASH splits
/// video and audio into two URLs, which must be opened together by the renderer.
/// </summary>
public interface IAudioTrackRenderer
{
    /// <summary>Attach an audio-only stream to the media that is being loaded.</summary>
    void SetMediaUrlWithAudio(string url, string start, string audioUrl);
}

public sealed record MediaSegment(string Url, double DurationSeconds);

public interface ISegmentedRenderer
{
    void SetMediaSegments(IReadOnlyList<MediaSegment> segments, string start);
}

public interface IRenderer : IDisposable
{
    string Name { get; }
    bool Available { get; }

    void Start();
    void Stop();

    void SetMediaStop();
    void SetMediaPause();
    void SetMediaResume();
    void SetMediaVolume(int volume);
    void SetMediaMute(bool mute);
    void SetMediaUrl(string url, string start = "0");
    void SetMediaTitle(string title);
    void SetMediaPosition(string position);
    void SetMediaSpeed(double speed);
    void SetMediaSubFile(string url, string title);
    void SetMediaSubShow(bool visible);
    void SetMediaText(string text, int durationMs = 1000);
}
