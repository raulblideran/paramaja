using System.Diagnostics;
using Paramaja.Core.Torrent;

namespace Paramaja.Core.Tests;

public class TorrentEnvelopeTests
{
    [Fact]
    public void Unwraps_ok_response()
    {
        var info = TorrentService.Unwrap<TorrentEngineInfo>("""{"ok":true,"data":{"http_addr":"127.0.0.1:1234","listen_addr":null}}""");
        Assert.Equal("127.0.0.1:1234", info.HttpAddress);
    }

    [Fact]
    public void Throws_error_message()
    {
        var ex = Assert.Throws<TorrentException>(() => TorrentService.Unwrap<TorrentInfo>("""{"ok":false,"error":"timed out resolving"}"""));
        Assert.Equal("timed out resolving", ex.Message);
    }

    [Fact]
    public void Parses_torrent_info()
    {
        var info = TorrentService.Unwrap<TorrentInfo>("""
            {"ok":true,"data":{"id":3,"info_hash":"ab","name":"Sintel","selected_file":1,
             "stream_url":"http://127.0.0.1:5/stream/3/1",
             "files":[{"index":0,"path":"a/Sintel.srt","len":10,"is_video":false},{"index":1,"path":"a/Sintel.mp4","len":99,"is_video":true}]}}
            """);
        Assert.Equal(3, info.Id);
        Assert.Equal("Sintel.mp4", info.Files[1].FileName);
        Assert.Equal(1, info.SelectedFile);
    }
}

/// <summary>Full C# → Rust → HTTP → mpv path using a local seeder (no internet).</summary>
[Collection("torrent-engine")]
public class TorrentIntegrationTests
{
    private static bool Available =>
        File.Exists(TestEnvironment.SeedExample) && TestEnvironment.HasMpv && TestEnvironment.HasFixtures;

    private sealed class Seeder : IDisposable
    {
        private readonly Process _p;
        public string Magnet { get; }

        public Seeder(string file)
        {
            _p = Process.Start(new ProcessStartInfo(TestEnvironment.SeedExample, $"\"{file}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            })!;
            var line = _p.StandardOutput.ReadLine() ?? throw new InvalidOperationException("seeder exited");
            Magnet = line["MAGNET ".Length..];
        }

        public void Dispose()
        {
            _p.StandardInput.Close();
            if (!_p.WaitForExit(3000)) _p.Kill();
        }
    }

    private static TorrentService StartEngine(string cache)
    {
        var svc = new TorrentService();
        svc.Start(new TorrentEngineConfig { CacheDir = cache, EnableDht = false, EnableTrackers = false, EnableUpnp = false });
        return svc;
    }

    [Fact]
    public async Task Magnet_streams_into_mpv_and_can_seek()
    {
        if (!Available) return;
        var dir = TestEnvironment.TempDir();
        var movie = Path.Combine(dir, "Movie.2026.mkv");
        File.Copy(TestEnvironment.Media("hdr10_hevc.mkv"), movie);
        using var seeder = new Seeder(movie);
        using var torrents = StartEngine(Path.Combine(TestEnvironment.TempDir(), "cache"));

        var info = await torrents.AddAsync(seeder.Magnet, TimeSpan.FromSeconds(30));
        Assert.Equal("Movie.2026.mkv", info.Name);
        Assert.NotNull(info.StreamUrl);

        using var h = new PlayerHarness();
        h.OpenAndWait(info.StreamUrl!);
        PlayerHarness.WaitUntil(() => h.Player.State.Transfer == "pq", "HDR stream metadata");
        PlayerHarness.WaitUntil(() => h.Player.State.VideoFormat == "hevc", "hevc over torrent");

        h.Player.SeekAbsolute(2.0);
        h.WaitRestart();
        PlayerHarness.WaitUntil(() => h.Player.State.Position >= 1.9, "seek within the torrent stream");

        var stats = torrents.GetStats(info.Id);
        Assert.True(stats.ProgressBytes > 0);
        Assert.Null(stats.Error);

        torrents.Remove(info.Id, deleteFiles: true);
        Assert.Throws<TorrentException>(() => torrents.GetStats(info.Id));
    }

    [Fact]
    public async Task Bad_and_unreachable_magnets_fail_cleanly()
    {
        if (!File.Exists(TestEnvironment.SeedExample)) return;
        using var torrents = StartEngine(Path.Combine(TestEnvironment.TempDir(), "cache"));
        var bad = await Assert.ThrowsAsync<TorrentException>(() => torrents.AddAsync("not a magnet", TimeSpan.FromSeconds(1)));
        Assert.Contains("magnet", bad.Message);
        var timeout = await Assert.ThrowsAsync<TorrentException>(() =>
            torrents.AddAsync("magnet:?xt=urn:btih:0000000000000000000000000000000000000002", TimeSpan.FromMilliseconds(700)));
        Assert.Contains("timed out", timeout.Message);
    }

    [Fact]
    public void Calls_before_start_throw()
    {
        using var svc = new TorrentService();
        Assert.Throws<TorrentException>(() => svc.GetStats(0));
    }
}
