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
        Assert.Equal(DeinterlaceAlgorithm.Bwdif, s.DeinterlaceAlgorithm);
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
        store.Save(new PlayerSettings { DeinterlaceAlgorithm = DeinterlaceAlgorithm.Bob, AudioPassthrough = true });
        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"Bob\"", json);
        var loaded = store.Load();
        Assert.Equal(DeinterlaceAlgorithm.Bob, loaded.DeinterlaceAlgorithm);
        Assert.True(loaded.AudioPassthrough);
    }
}
