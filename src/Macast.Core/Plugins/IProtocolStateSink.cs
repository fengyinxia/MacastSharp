namespace Macast.Core.Plugins;

/// <summary>
/// The part of the protocol the renderers are allowed to touch. Both the DLNA
/// protocol and the NVA protocol expose the same state variables, so a renderer
/// never needs to know which one is active.
/// </summary>
public interface IProtocolStateSink
{
    string GetState(string name);

    void SetState(string name, string value);

    void SetState(string name, bool value);

    void SetState(string name, int value);
}
