using Paramaja.Core.Input;
using Paramaja.Core.Playback;

namespace Paramaja.Core.Tests;

public class InputTests
{
    [Theory]
    [InlineData("s", false, false, "s")]
    [InlineData("s", false, true, "S")]
    [InlineData("S", false, false, "s")]
    [InlineData("v", true, false, "Ctrl+v")]
    [InlineData("RIGHT", true, false, "Ctrl+RIGHT")]
    [InlineData(".", false, true, ".")]
    [InlineData("SPACE", false, false, "SPACE")]
    [InlineData("LEFT", false, true, "Shift+LEFT")]
    public void Canonical_key_names(string key, bool ctrl, bool shift, string expected) =>
        Assert.Equal(expected, ShortcutMap.Canonical(key, ctrl, shift));

    [Theory]
    [InlineData(".", PlayerAction.FrameStep)]
    [InlineData(",", PlayerAction.FrameBackStep)]
    [InlineData("s", PlayerAction.Screenshot)]
    [InlineData("S", PlayerAction.ScreenshotWithSubtitles)]
    [InlineData("SPACE", PlayerAction.TogglePause)]
    [InlineData("Ctrl+v", PlayerAction.PasteAndPlay)]
    [InlineData("D", PlayerAction.ToggleDownmix)]
    [InlineData("d", PlayerAction.ToggleDeinterlace)]
    [InlineData("MBTN_LEFT_DBL", PlayerAction.ToggleFullscreen)]
    public void Default_bindings(string key, PlayerAction action)
    {
        Assert.True(ShortcutMap.TryGet(key, out var a));
        Assert.Equal(action, a);
    }

    [Fact]
    public void Every_action_has_a_shortcut()
    {
        var bound = ShortcutMap.Default.Values.ToHashSet();
        foreach (var action in Enum.GetValues<PlayerAction>()) Assert.Contains(action, bound);
    }

    [Fact]
    public void Shortcut_descriptions_are_human_readable()
    {
        var d = ShortcutMap.Describe().ToDictionary(x => x.Action, x => x.Keys);
        Assert.Equal("S", d[PlayerAction.Screenshot]);
        Assert.Equal("Shift+S", d[PlayerAction.ScreenshotWithSubtitles]);
        Assert.Contains("Ctrl+V", d[PlayerAction.PasteAndPlay]);
        Assert.Contains("Double-click", d[PlayerAction.ToggleFullscreen]);
    }

    [Fact]
    public void Classifies_magnets_hashes_urls_and_files()
    {
        const string hash = "08ada5a7a6183aae1e09d831df6748d566095a10";
        Assert.Equal(InputKind.Magnet, InputClassifier.Classify($"  magnet:?xt=urn:btih:{hash} ").Kind);
        Assert.Equal((InputKind.Magnet, $"magnet:?xt=urn:btih:{hash}"), InputClassifier.Classify(hash.ToUpperInvariant()));
        Assert.Equal(InputKind.Url, InputClassifier.Classify("https://example.com/video.mp4").Kind);
        Assert.Equal(InputKind.None, InputClassifier.Classify("hello world").Kind);
        Assert.Equal(InputKind.None, InputClassifier.Classify(null).Kind);
        Assert.Equal(InputKind.None, InputClassifier.Classify("javascript:alert(1)").Kind);

        var file = Path.Combine(TestEnvironment.TempDir(), "clip.mkv");
        File.WriteAllText(file, "");
        Assert.Equal((InputKind.File, file), InputClassifier.Classify($"\"{file}\""));
        Assert.Equal((InputKind.File, file), InputClassifier.Classify(new Uri(file).AbsoluteUri));
    }

    [Fact]
    public void Dvd_folders_are_recognised_from_the_disc_its_video_ts_or_its_ifo()
    {
        var disc = Path.Combine(TestEnvironment.TempDir(), "MY_MOVIE");
        var videoTs = Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS")).FullName;
        File.WriteAllText(Path.Combine(videoTs, "VIDEO_TS.IFO"), "");
        File.WriteAllText(Path.Combine(videoTs, "VTS_01_1.VOB"), "");
        Assert.Equal((InputKind.Dvd, disc), InputClassifier.Classify(disc));
        Assert.Equal((InputKind.Dvd, disc), InputClassifier.Classify(disc + Path.DirectorySeparatorChar));
        Assert.Equal((InputKind.Dvd, disc), InputClassifier.Classify(videoTs));
        Assert.Equal((InputKind.Dvd, disc), InputClassifier.Classify(Path.Combine(videoTs, "VIDEO_TS.IFO")));
        // A single VOB still plays on its own.
        Assert.Equal(InputKind.File, InputClassifier.Classify(Path.Combine(videoTs, "VTS_01_1.VOB")).Kind);
    }

    [Fact]
    public void Bluray_folders_are_recognised_from_the_disc_its_bdmv_or_a_playlist()
    {
        var disc = Path.Combine(TestEnvironment.TempDir(), "MOVIE_BD");
        var bdmv = Directory.CreateDirectory(Path.Combine(disc, "BDMV")).FullName;
        var playlists = Directory.CreateDirectory(Path.Combine(bdmv, "PLAYLIST")).FullName;
        File.WriteAllText(Path.Combine(bdmv, "index.bdmv"), "");
        File.WriteAllText(Path.Combine(playlists, "00800.mpls"), "");
        Assert.Equal((InputKind.Bluray, disc), InputClassifier.Classify(disc));
        Assert.Equal((InputKind.Bluray, disc), InputClassifier.Classify(bdmv));
        Assert.Equal((InputKind.Bluray, disc), InputClassifier.Classify(Path.Combine(bdmv, "index.bdmv")));
        Assert.Equal((InputKind.Bluray, disc), InputClassifier.Classify(Path.Combine(playlists, "00800.mpls")));
    }

    [Fact]
    public void Other_folders_play_as_a_playlist()
    {
        var dir = TestEnvironment.TempDir();
        Assert.Equal((InputKind.Folder, dir), InputClassifier.Classify(dir));
        Assert.False(InputClassifier.IsStreaming(InputKind.Folder));
    }

    [Fact]
    public void Track_labels()
    {
        Assert.Equal("1: Commentary [eng] AC3 5.1", new TrackInfo(1, TrackType.Audio, "Commentary", "eng", "ac3", false, false, false, 6).Label);
        Assert.Equal("3: SUBRIP (external)", new TrackInfo(3, TrackType.Subtitle, null, null, "subrip", false, true, false, null).Label);
        Assert.Equal("7.1", TrackInfo.ChannelLabel(8));
    }

    [Fact]
    public void Screenshot_names_are_safe_and_timestamped()
    {
        var name = PlayerService.ScreenshotFileName("A: Movie / Part?2", 3725.25, new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal("A_ Movie _ Part_2_01-02-05.250_20260102-030405.png", name);
        Assert.StartsWith("screenshot_", PlayerService.ScreenshotFileName(null, 0, DateTime.Now));
        Assert.True(PlayerService.ScreenshotFileName(new string('x', 500), 0, DateTime.Now).Length < 120);
    }
}
