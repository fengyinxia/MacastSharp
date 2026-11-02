using System.Text.Json.Nodes;

namespace Macast.Core.Protocols.Nva;

// One renderer means one playback state, regardless of how many control sockets are open.
internal sealed class NvaPlaybackState
{
    public readonly object Gate = new();
    public string Aid = string.Empty;
    public string Oid = string.Empty;
    public string Cid = string.Empty;
    public string RoomId = "0";
    public string AccessKey = string.Empty;
    public string Title = string.Empty;
    public string EpisodeTitle = string.Empty;
    public int Epid;
    public int SeasonId;
    public int CurrentQn;
    public int DesireQn;
    public int PlayUrlType = 1;
    public int ContentType = 1;
    public bool IsPortrait;
    public double Speed = 1;
    public JsonObject Qn = new();
}
