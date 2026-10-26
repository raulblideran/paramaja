using NegsPlayer.Core.Playback;
using NegsPlayer.Core.Settings;

namespace NegsPlayer.Core.Tests;

/// <summary>A real libmpv player with no window and no audio device.</summary>
public sealed class PlayerHarness : IDisposable
{
    public PlayerService Player { get; }
    public PortablePaths Paths { get; }
    private readonly SemaphoreSlim _loaded = new(0);
    private readonly SemaphoreSlim _restart = new(0);

    public PlayerHarness(PlayerSettings? settings = null, params (string, string)[] extra)
    {
        Paths = new PortablePaths(TestEnvironment.TempDir());
        var options = new List<KeyValuePair<string, string>> { new("vo", "null"), new("ao", "null"), new("hwdec", "no") };
        options.AddRange(extra.Select(e => new KeyValuePair<string, string>(e.Item1, e.Item2)));
        Player = new PlayerService(settings ?? new PlayerSettings(), Paths, null, options);
        Player.FileLoaded += () => _loaded.Release();
        Player.Mpv.PlaybackRestart += () => _restart.Release();
    }

    public void OpenAndWait(string path, bool paused = false)
    {
        Player.Open(path);
        Assert.True(_loaded.Wait(TimeSpan.FromSeconds(20)), $"timed out loading {path}");
        if (paused) Player.SetPaused(true);
        WaitRestart();
    }

    public void WaitRestart() => Assert.True(_restart.Wait(TimeSpan.FromSeconds(20)), "timed out waiting for playback restart");

    public static void WaitUntil(Func<bool> condition, string what, double seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for {what}");
            Thread.Sleep(25);
        }
    }

    public void Dispose() => Player.Dispose();
}
