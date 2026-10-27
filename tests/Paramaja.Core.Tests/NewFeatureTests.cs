using Paramaja.Core.Input;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;
using Paramaja.Core.Torrent;

namespace Paramaja.Core.Tests;

public class VolumeCurveTests
{
    [Theory]
    [InlineData(100, 0)]
    [InlineData(50, -30)]
    [InlineData(75, -15)]
    [InlineData(25, -45)]
    public void Slider_moves_in_equal_decibel_steps(double level, double db) =>
        Assert.Equal(db, VolumeCurve.ToDecibels(level), 6);

    [Fact]
    public void Endpoints()
    {
        Assert.Equal(0, VolumeCurve.ToMpv(0));
        Assert.Equal(100, VolumeCurve.ToMpv(100), 6);
        Assert.Equal(150, VolumeCurve.ToMpv(150), 6);
        Assert.Equal(150, VolumeCurve.ToMpv(9999), 6);
        Assert.Equal("muted", VolumeCurve.Describe(0));
        Assert.Equal("0 dB", VolumeCurve.Describe(100));
        Assert.Equal("-30.0 dB", VolumeCurve.Describe(50));
        Assert.Equal("+10.6 dB", VolumeCurve.Describe(150));
    }

    [Fact]
    public void Is_monotonic_and_round_trips()
    {
        var prev = -1.0;
        for (var level = 0.0; level <= 150; level += 0.5)
        {
            var mpv = VolumeCurve.ToMpv(level);
            Assert.True(mpv > prev || level == 0, $"not increasing at {level}");
            prev = mpv;
            Assert.Equal(level, VolumeCurve.FromMpv(mpv), 1);
        }
    }

    [Fact]
    public void Halfway_is_much_quieter_than_mpvs_linear_halfway()
    {
        // The complaint was that the bottom half of the slider did almost nothing.
        Assert.True(VolumeCurve.ToMpv(50) < 50);
        Assert.True(VolumeCurve.ToMpv(90) > 75, "top of the range still moves in small steps");
    }
}

public class QuipTests
{
    [Fact]
    public void Includes_the_requested_lines()
    {
        Assert.Contains("Big fast, many speed", BufferingQuips.All);
        Assert.Contains("Bruney", BufferingQuips.All);
    }

    [Fact]
    public void Never_repeats_back_to_back()
    {
        var last = BufferingQuips.Next();
        for (var i = 0; i < 200; i++)
        {
            var next = BufferingQuips.Next(last);
            Assert.NotEqual(last, next);
            last = next;
        }
    }
}

public class LinkClassifierTests
{
    [Theory]
    [InlineData("https://mega.nz/file/AbC#key")]
    [InlineData("https://mega.co.nz/#!AbC!key")]
    [InlineData("  https://www.mega.nz/file/AbC#key ")]
    public void Mega_links_go_to_the_engine(string link) =>
        Assert.Equal(InputKind.Mega, InputClassifier.Classify(link).Kind);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ")]
    [InlineData("https://youtu.be/aqz-KE-bpKQ")]
    [InlineData("https://vimeo.com/1084537")]
    [InlineData("https://example.com/film.mkv")]
    public void Web_links_are_urls(string link) =>
        Assert.Equal(InputKind.Url, InputClassifier.Classify(link).Kind);

    [Fact]
    public void Streaming_kinds()
    {
        Assert.True(InputClassifier.IsStreaming(InputKind.Mega));
        Assert.True(InputClassifier.IsStreaming(InputKind.Url));
        Assert.True(InputClassifier.IsStreaming(InputKind.Magnet));
        Assert.False(InputClassifier.IsStreaming(InputKind.File));
    }

    [Fact]
    public void Mega_info_parses()
    {
        var m = TorrentService.Unwrap<MegaInfo>("""{"ok":true,"data":{"id":2,"name":"Clip.mkv","size":123,"stream_url":"http://127.0.0.1:5/mega/2"}}""");
        Assert.Equal("Clip.mkv", m.Name);
        Assert.Equal("http://127.0.0.1:5/mega/2", m.StreamUrl);
    }
}

public class YtDlpTests
{
    [Fact]
    public void Detects_bundled_binaries_next_to_the_app()
    {
        var dir = TestEnvironment.TempDir();
        var y = new YtDlp(dir);
        Assert.Null(y.BundledPath);
        Assert.Null(y.BundledJsRuntime);
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        File.WriteAllText(Path.Combine(dir, "yt-dlp" + exe), "");
        Assert.Equal(Path.Combine(dir, "yt-dlp" + exe), y.BundledPath);
    }

    [Theory]
    [InlineData("PARAMAJA 512|2048|1048576|3|avc1.640028", 25.0, false)]
    [InlineData("PARAMAJA 100|NA|NA|NA|none", null, true)]
    public void Progress_lines_are_parsed(string line, double? percent, bool audio)
    {
        var p = DownloadProgress.Parse(line)!;
        Assert.Equal(percent, p.Percent);
        Assert.Equal(audio, p.IsAudio);
        Assert.StartsWith(audio ? "Downloading audio" : "Downloading video", p.ToString());
        Assert.Null(DownloadProgress.Parse("[download] Destination: x.mp4"));
    }

    [Fact]
    public void Qualities_are_read_from_yt_dlps_format_list()
    {
        // Trimmed from real yt-dlp -J output: audio-only and storyboard formats are skipped.
        const string json = """
            { "title": "Big Buck Bunny", "formats": [
              { "format_id": "sb0", "vcodec": "none", "height": 45 },
              { "format_id": "251", "vcodec": "none", "acodec": "opus" },
              { "format_id": "160", "vcodec": "avc1", "height": 144 },
              { "format_id": "399", "vcodec": "av01", "height": 1080 },
              { "format_id": "137", "vcodec": "avc1", "height": 1080 },
              { "format_id": "401", "vcodec": "av01", "height": 2160 },
              { "format_id": "18", "vcodec": "avc1", "height": null } ] }
            """;
        var info = YtDlp.ParseInfo(json);
        Assert.Equal("Big Buck Bunny", info.Title);
        Assert.Equal([2160, 1080, 144], info.Heights);
        // A site that offers one file has no format list.
        Assert.Equal([720], YtDlp.ParseInfo("""{ "title": "x", "height": 720 }""").Heights);
    }

    [Theory]
    [InlineData(0, "bv*+ba/b")]
    [InlineData(720, "bv*[height<=720]+ba/b[height<=720]/bv*+ba/b")]
    public void Download_takes_the_chosen_quality(int maxHeight, string format)
    {
        var args = new YtDlp(TestEnvironment.TempDir()).DownloadArguments("https://youtu.be/x", "/dl", "/tmp/p.txt", maxHeight).ToList();
        Assert.Equal(format, args[args.IndexOf("-f") + 1]);
    }

    [Fact]
    public void Download_merges_best_video_and_audio_with_the_bundled_tools()
    {
        var dir = TestEnvironment.TempDir();
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var f in new[] { "yt-dlp", "qjs", "ffmpeg" }) File.WriteAllText(Path.Combine(dir, f + exe), "");
        var args = new YtDlp(dir).DownloadArguments("https://youtu.be/x", "/dl", "/tmp/p.txt").ToList();
        Assert.Equal("bv*+ba/b", args[args.IndexOf("-f") + 1]);
        Assert.Equal("mkv", args[args.IndexOf("--merge-output-format") + 1]);
        Assert.Equal(Path.Combine(dir, "ffmpeg" + exe), args[args.IndexOf("--ffmpeg-location") + 1]);
        Assert.Equal("quickjs:" + Path.Combine(dir, "qjs" + exe), args[args.IndexOf("--js-runtimes") + 1]);
        Assert.Equal("/dl", args[args.IndexOf("-P") + 1]);
        Assert.Equal(["--", "https://youtu.be/x"], args[^2..]);
    }

    /// <summary>A stand-in yt-dlp: reports progress, then saves a file (or hangs, to be cancelled).</summary>
    private static YtDlp FakeYtDlp(bool hang)
    {
        var dir = TestEnvironment.TempDir();
        var script = Path.Combine(dir, "yt-dlp");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            while [ $# -gt 0 ]; do
              case "$1" in
                --print-to-file) print="$3"; shift 2 ;;
                -P) out="$2"; shift ;;
                -o) name="$2"; shift ;;
                -s) dry=1 ;;
              esac
              shift
            done
            # The dry run: the resolution, then where yt-dlp would save.
            [ -n "$dry" ] && { echo 1080; echo "$out/clip.mkv"; exit 0; }
            echo "[download] Destination: $out/clip.f137.mp4"
            echo "partial" > "$out/clip.f137.mp4.part"
            echo "PARAMAJA 50|100|10|1|avc1"
            {{(hang ? "sleep 30" : "")}}
            echo "video" > "$out/$name"
            echo "$out/$name" >> "$print"
            """.Replace("\r", ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new YtDlp(dir);
    }

    [Fact]
    public async Task Download_reports_progress_and_returns_the_saved_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var downloads = TestEnvironment.TempDir();
        var seen = new List<DownloadProgress>();
        var file = await FakeYtDlp(hang: false).DownloadAsync("https://youtu.be/x", downloads, seen.Add);
        Assert.Equal(Path.Combine(downloads, "clip.mkv"), file);
        Assert.True(File.Exists(file));
        Assert.Equal(50, Assert.Single(seen).Percent);
    }

    [Fact]
    public async Task Downloading_the_same_video_again_never_overwrites()
    {
        if (OperatingSystem.IsWindows()) return;
        var downloads = TestEnvironment.TempDir();
        var y = FakeYtDlp(hang: false);
        var names = new List<string>();
        for (var i = 0; i < 3; i++) names.Add(Path.GetFileName(await y.DownloadAsync("https://youtu.be/x", downloads)));
        Assert.Equal(["clip.mkv", "clip 1080p.mkv", "clip 1080p (1).mkv"], names);
    }

    [Fact]
    public void Audio_only_takes_the_best_audio_stream_as_it_is()
    {
        var args = new YtDlp(TestEnvironment.TempDir()).DownloadArguments("https://youtu.be/x", "/dl", "/tmp/p.txt", audioOnly: true).ToList();
        Assert.Equal("ba/b", args[args.IndexOf("-f") + 1]);
        Assert.Contains("-x", args);
        Assert.Equal("best", args[args.IndexOf("--audio-format") + 1]); // no re-encoding
        Assert.DoesNotContain("--merge-output-format", args);
        Assert.Contains("--embed-metadata", args);
        Assert.Equal(["--", "https://youtu.be/x"], args[^2..]);
    }

    [Fact]
    public void Audio_names_are_numbered_whatever_the_extension()
    {
        var dir = TestEnvironment.TempDir();
        Assert.Equal("Song [id]", YtDlp.UniqueStem(dir, "Song [id]"));
        File.WriteAllText(Path.Combine(dir, "Song [id].m4a"), "");
        Assert.Equal("Song [id] (1)", YtDlp.UniqueStem(dir, "Song [id]"));
        File.WriteAllText(Path.Combine(dir, "Song [id] (1).opus"), "");
        Assert.Equal("Song [id] (2)", YtDlp.UniqueStem(dir, "Song [id]"));
    }

    [Fact]
    public void Clashing_names_get_the_resolution_then_a_counter()
    {
        var dir = TestEnvironment.TempDir();
        Assert.Equal("Film [id].mkv", YtDlp.UniqueFileName(dir, "Film [id].mkv", 1080));
        File.WriteAllText(Path.Combine(dir, "Film [id].mkv"), "");
        Assert.Equal("Film [id] 720p.mkv", YtDlp.UniqueFileName(dir, "Film [id].mkv", 720));
        File.WriteAllText(Path.Combine(dir, "Film [id] 720p.mkv"), "");
        Assert.Equal("Film [id] 720p (1).mkv", YtDlp.UniqueFileName(dir, "Film [id].mkv", 720));
        File.WriteAllText(Path.Combine(dir, "Film [id] 720p (1).mkv"), "");
        Assert.Equal("Film [id] 720p (2).mkv", YtDlp.UniqueFileName(dir, "Film [id].mkv", 720));
        Assert.Equal("Film [id] 1080p.mkv", YtDlp.UniqueFileName(dir, "Film [id].mkv", 1080));
    }

    [Fact]
    public void Downloads_carry_the_metadata_and_chapters_and_a_cover_where_ffprobe_is_bundled()
    {
        var dir = TestEnvironment.TempDir();
        var y = new YtDlp(dir);
        var args = y.DownloadArguments("https://youtu.be/x", "/dl", "/tmp/p.txt").ToList();
        Assert.Contains("--embed-metadata", args);
        Assert.Contains("--embed-chapters", args);
        Assert.DoesNotContain("--embed-thumbnail", args); // needs ffprobe, which would fail the download
        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"), "");
        Assert.Contains("--embed-thumbnail", y.DownloadArguments("https://youtu.be/x", "/dl", "/tmp/p.txt").ToList());
    }

    [Fact]
    public async Task Cancelling_a_download_stops_it_and_deletes_the_partial_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var downloads = TestEnvironment.TempDir();
        using var cts = new CancellationTokenSource();
        var task = FakeYtDlp(hang: true).DownloadAsync("https://youtu.be/x", downloads, _ => cts.Cancel(), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public void Downloads_folder_follows_xdg_user_dirs()
    {
        var f = Path.Combine(TestEnvironment.TempDir(), "user-dirs.dirs");
        File.WriteAllText(f, "# comment\nXDG_DOWNLOAD_DIR=\"$HOME/Descărcări\"\n");
        Assert.Equal("/home/me/Descărcări", PortablePaths.XdgDownloads(f, "/home/me"));
        File.WriteAllText(f, "XDG_DOWNLOAD_DIR=\"$HOME/\"\n"); // disabled
        Assert.Null(PortablePaths.XdgDownloads(f, "/home/me"));
        Assert.False(string.IsNullOrEmpty(new PortablePaths(TestEnvironment.TempDir()).Downloads));
    }

    [Fact]
    public async Task Update_without_a_bundled_copy_says_so()
    {
        var y = new YtDlp(TestEnvironment.TempDir());
        await Assert.ThrowsAsync<InvalidOperationException>(() => y.UpdateAsync());
    }
}

public class NewSettingsTests
{
    [Fact]
    public void Defaults_keep_the_original_audio()
    {
        var s = new PlayerSettings();
        Assert.False(s.DownmixToStereo);
        Assert.False(s.AudioPassthrough);
    }

    [Fact]
    public void Passthrough_wins_over_downmix_when_both_are_set()
    {
        var s = new PlayerSettings { AudioPassthrough = true, DownmixToStereo = true };
        s.Normalize();
        Assert.False(s.DownmixToStereo);
    }

    [Fact]
    public void New_settings_round_trip()
    {
        var store = new SettingsStore(Path.Combine(TestEnvironment.TempDir(), "settings.json"));
        store.Save(new PlayerSettings { AudioPassthrough = true });
        var loaded = store.Load();
        Assert.True(loaded.AudioPassthrough);
    }

    [Fact]
    public void Settings_from_before_bob_was_removed_still_load()
    {
        var store = new SettingsStore(Path.Combine(TestEnvironment.TempDir(), "settings.json"));
        File.WriteAllText(store.FilePath, """{ "deinterlaceAlgorithm": "Bob", "audioPassthrough": true }""");
        Assert.True(store.Load().AudioPassthrough);
        Assert.False(File.Exists(store.FilePath + ".corrupt"));
    }
}

public class DiscEngineCacheTests
{
    [Fact]
    public void Plugin_cache_stamp_changes_when_a_plugin_file_does()
    {
        var plugins = Path.Combine(Path.GetTempPath(), "paramaja-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(plugins, "access"));
        var plugin = Path.Combine(plugins, "access", "libcdda_plugin.dll");
        File.WriteAllText(plugin, "x");
        File.WriteAllText(Path.Combine(plugins, "plugins.dat"), "cache"); // not a plugin: not stamped
        var stamp = VlcDiscPlayer.PluginStamp(plugins);
        Assert.Equal(stamp, VlcDiscPlayer.PluginStamp(plugins));
        File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow.AddDays(-3)); // as unzipping a new version over does
        Assert.NotEqual(stamp, VlcDiscPlayer.PluginStamp(plugins));
    }
}
