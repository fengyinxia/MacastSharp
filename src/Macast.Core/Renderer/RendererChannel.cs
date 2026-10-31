namespace Macast.Core.Renderer;

public sealed record RendererChannel(string Name, string Url);

/// <summary>A renderer that can offer an internal playlist (the Live plugin).</summary>
public interface IChannelListProvider
{
    IReadOnlyList<RendererChannel> Channels { get; }

    Task<int> RefreshAsync(CancellationToken cancellationToken = default);

    void Play(RendererChannel channel);
}
