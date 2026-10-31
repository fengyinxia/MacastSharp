namespace Macast.Core.Renderer;

/// <summary>
/// Describes how an mpv-compatible player has to be launched. The default profile
/// is the bundled mpv; IINA uses the same IPC protocol with its own CLI switch.
/// </summary>
public sealed class MpvLaunchProfile
{
    public string Name { get; init; } = "MPV";

    /// <summary>Absolute executable path. Null means "resolve from settings / PATH".</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>IINA exposes the same socket switch under a different name.</summary>
    public string IpcServerArgument { get; init; } = "--input-ipc-server";

    public IReadOnlyList<string> ExtraArguments { get; init; } = Array.Empty<string>();

    /// <summary>mpv window geometry / hwdec / osc switches. IINA brings its own UI.</summary>
    public bool UseStandardWindowArguments { get; init; } = true;
}
