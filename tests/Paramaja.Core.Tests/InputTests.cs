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
    public void A_cd_rip_folder_plays_its_cue_sheet()
    {
        var dir = TestEnvironment.TempDir();
        File.WriteAllText(Path.Combine(dir, "01 - Jam.flac"), "");
        // EAC names the .wav it ripped; the files are FLAC by now. Both play, but the cue sheet
        // naming the files that are there wins.
        File.WriteAllText(Path.Combine(dir, "A.cue"), "FILE \"01 - Jam.wav\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");
        File.WriteAllText(Path.Combine(dir, "B.flactracks.cue"), "REM x\nFILE \"01 - Jam.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");
        Assert.Equal((InputKind.File, Path.Combine(dir, "B.flactracks.cue")), InputClassifier.Classify(dir));
        File.Delete(Path.Combine(dir, "B.flactracks.cue"));
        Assert.Equal((InputKind.File, Path.Combine(dir, "A.cue")), InputClassifier.Classify(dir)); // its .wav is a .flac now

        // A folder with videos stays a playlist, cue sheet or not.
        File.WriteAllText(Path.Combine(dir, "concert.mkv"), "");
        Assert.Equal((InputKind.Folder, dir), InputClassifier.Classify(dir));
    }

    [Fact]
    public void Cue_sheets_are_read_the_way_rippers_write_them()
    {
        // EAC's "gaps appended": a track's FILE line comes after its INDEX 00, before its INDEX 01;
        // a TITLE may also follow the INDEX.
        var sheet = CueSheet.Parse([
            "TITLE \"Album\"", "FILE \"a.wav\" WAVE", "  TRACK 01 AUDIO", "    TITLE \"One\"", "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO", "    INDEX 00 03:59:70", "FILE \"b.wav\" WAVE", "    INDEX 01 00:00:00", "    TITLE \"Two\"",
            "FILE image.flac WAVE", "  TRACK 03 AUDIO", "    INDEX 01 01:02:15"]);
        Assert.Equal("Album", sheet.Title);
        Assert.Equal([new CueSheet.Track("a.wav", 0, "One", "WAVE", "AUDIO"), new CueSheet.Track("b.wav", 0, "Two", "WAVE", "AUDIO"),
                new CueSheet.Track("image.flac", 62.2, null, "WAVE", "AUDIO")],
            sheet.Tracks);
    }

    [Fact]
    public void Cue_sheet_files_are_found_by_another_extension_another_case_or_without_their_windows_folder()
    {
        var dir = TestEnvironment.TempDir();
        File.WriteAllText(Path.Combine(dir, "01 - HIStory.flac"), "");
        Assert.Equal(Path.Combine(dir, "01 - HIStory.flac"), CueSheet.Resolve(dir, "01 - History.wav"));
        Assert.Equal(Path.Combine(dir, "01 - HIStory.flac"), CueSheet.Resolve(dir, @"Rip [FLAC]\01 - HIStory.wav"));
        Assert.Null(CueSheet.Resolve(dir, "02 - Missing.wav"));

        // Every track found: an EDL with the files really there, one chapter per track.
        File.WriteAllText(Path.Combine(dir, "rip.cue"), "FILE \"01 - History.wav\" WAVE\n  TRACK 01 AUDIO\n    TITLE \"A, B; C\"\n    INDEX 01 00:00:00\n");
        var url = CueSheet.PlayableUrl(Path.Combine(dir, "rip.cue"));
        var file = Path.Combine(dir, "01 - HIStory.flac");
        Assert.Equal($"edl://%{System.Text.Encoding.UTF8.GetByteCount(file)}%{file},title=%7%A, B; C", url);

        // A track missing: left to mpv as it is.
        File.AppendAllText(Path.Combine(dir, "rip.cue"), "FILE \"02 - Missing.wav\" WAVE\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");
        Assert.Null(CueSheet.PlayableUrl(Path.Combine(dir, "rip.cue")));
    }

    [Fact]
    public void Windows_audio_cd_tracks_open_the_cd()
    {
        var drive = TestEnvironment.TempDir();
        File.WriteAllText(Path.Combine(drive, "Track01.cda"), "");
        Assert.Equal((InputKind.AudioCd, drive), InputClassifier.Classify(drive));
        Assert.True(OpticalDisc.HasCdaTracks(drive));
        Assert.Equal(DiscContent.Unknown, OpticalDisc.Probe("/nonexistent/sr9"));
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
