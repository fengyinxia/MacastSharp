using System.Text;
using Macast.Core;
using Macast.Core.Settings;
using Macast.Core.Utils;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // Some redirected consoles reject the encoding change.
}

var rawArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (rawArgs.Contains("--help") || rawArgs.Contains("-h"))
{
    Console.WriteLine("""
        macast-cli [options]
          --port N       HTTP port (0 = random, default from config)
          --no-mpv       do not start mpv (protocol simulation mode)
          --loopback     bind discovery/description to loopback (testing)
          --selftest     run an end-to-end protocol self test
          --plugins      list the C# plugins and their availability
          --potplayer    real-machine PotPlayer check (play/pause/mute/seek + screenshots)
          --probe-playurl  resolve a bilibili playurl (see --help in PlayUrlProbe.cs)
          --renderer ID  override Macast_Renderer for this run (MPV, PotPlayer)
          --protocol ID  automatic DLNA + NVA (Auto; legacy DLNA/NVA accepted)
          --help         show this help
        """);
    return 0;
}

if (rawArgs.Contains("--selftest"))
{
    return await Macast.Cli.SelfTest.RunAsync();
}

if (rawArgs.Contains("--plugins"))
{
    return Macast.Cli.PluginList.Run();
}

if (rawArgs.Contains("--potplayer"))
{
    return Macast.Cli.PotPlayerProbe.Run(rawArgs);
}

if (rawArgs.Contains("--probe-playurl"))
{
    return await Macast.Cli.PlayUrlProbe.RunAsync(rawArgs);
}

Log.LogFile = Path.Combine(AppSettings.ConfigDir, "macast-cs.log");
Log.ConfigureRetention(AppSettings.Load().GetInt("LogRetentionDays", Log.DefaultRetentionDays));
Log.Info($"Macast C# starting, version {AssetPaths.Version}");

int? port = null;
for (var i = 0; i < rawArgs.Length; i++)
{
    if (rawArgs[i] == "--port" && i + 1 < rawArgs.Length && int.TryParse(rawArgs[i + 1], out var parsed))
    {
        port = parsed;
        i++;
    }
}

var noMpv = rawArgs.Contains("--no-mpv");
var loopback = rawArgs.Contains("--loopback");

for (var i = 0; i < rawArgs.Length; i++)
{
    if (rawArgs[i] == "--renderer" && i + 1 < rawArgs.Length)
    {
        Environment.SetEnvironmentVariable("MACAST_RENDERER", rawArgs[i + 1]);
        i++;
    }
    else if (rawArgs[i] == "--protocol" && i + 1 < rawArgs.Length)
    {
        Environment.SetEnvironmentVariable("MACAST_PROTOCOL", rawArgs[i + 1]);
        i++;
    }
}

try
{
    await using var app = new MacastApp(noMpv, loopback);
    await app.StartAsync(port);

    Console.WriteLine();
    Console.WriteLine("Macast is running.");
    Console.WriteLine($"  Device : {app.Settings.FriendlyName}");
    Console.WriteLine($"  Address: {app.DeviceUrl()}");
    Console.WriteLine($"  HTTP   : http://127.0.0.1:{app.Port}");
    Console.WriteLine($"  mpv    : {(app.Renderer.Available ? "running" : "simulated (use --no-mpv to silence)")}");
    Console.WriteLine("Press Ctrl+C to stop.");
    Console.WriteLine();

    var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        exit.TrySetResult();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.TrySetResult();

    await exit.Task;
    Log.Info("shutdown requested");
    await app.StopAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Error($"fatal: {ex}");
    Console.Error.WriteLine("Macast failed to start: " + ex.Message);
    return 1;
}
