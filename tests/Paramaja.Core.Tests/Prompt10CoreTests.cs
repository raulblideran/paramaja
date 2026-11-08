using System.Buffers.Binary;
using System.Text;
using Paramaja.Core.Input;
using Paramaja.Core.Native;
using Paramaja.Core.Playback;
using Paramaja.Core.Ripping.Dvd;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Tests;

/// <summary>DVD rips of chosen chapters, and x264's BWDIF (1.12.0).</summary>
public class Prompt10RipTests
{
    private static readonly DvdTitle Twelve = new(2, 1, 12, 1, TimeSpan.FromMinutes(95), [], []);
    private static DvdRipOptions Options(IReadOnlyList<int>? chapters, bool files = false) =>
        new() { Source = "/x.iso", Title = 2, OutputRoot = "/out", Chapters = chapters, ChapterFiles = files };

    [Fact]
    public void Every_chapter_in_one_file_is_the_whole_title_as_it_always_was()
    {
        Assert.Null(DvdRipper.Selection(Options(null), Twelve));
        Assert.Null(DvdRipper.Selection(Options(Enumerable.Range(1, 12).ToList()), Twelve));
        // One file per chapter needs them one by one, all of them included.
        Assert.Equal(12, DvdRipper.Selection(Options(Enumerable.Range(1, 12).ToList(), files: true), Twelve)!.Count);
        Assert.Equal([2, 3, 7], DvdRipper.Selection(Options([7, 3, 2, 3, 40]), Twelve));
        Assert.Throws<ArgumentException>(() => DvdRipper.Selection(Options([40]), Twelve));
    }

    [Fact]
    public void Chapters_next_to_each_other_are_read_in_one_go()
    {
        Assert.Equal([new ChapterRun(2, 4), new ChapterRun(7, 7), new ChapterRun(9, 10)], DvdRipper.Runs([2, 3, 4, 7, 9, 10]));
        Assert.Equal("Chapter 3", DvdRipper.ChaptersLabel([3]));
        Assert.Equal("Chapters 3-5", DvdRipper.ChaptersLabel([3, 4, 5]));
        Assert.Equal("Chapters 2, 4-6, 9", DvdRipper.ChaptersLabel([2, 4, 5, 6, 9]));
    }

    [Fact]
    public void A_range_is_read_with_ffmpegs_chapter_options_and_the_whole_title_without()
    {
        var o = Options([3, 4, 5]);
        var whole = DvdRipper.MkvArguments(o, preindex: true, lpcm: [], "T", "/out/T.mkv").ToList();
        Assert.DoesNotContain("-chapter_start", whole);
        Assert.DoesNotContain("-chapter_end", whole);
        var range = DvdRipper.MkvArguments(o, preindex: true, lpcm: [], "T", "/out/T.mkv", range: new ChapterRun(3, 5)).ToList();
        Assert.Equal(["-f", "dvdvideo", "-title", "2", "-angle", "1", "-chapter_start", "3", "-chapter_end", "5", "-preindex", "1", "-i", "/x.iso"],
            range.SkipWhile(a => a != "-f").TakeWhile(a => a != "-map"));
    }

    [Fact]
    public void The_picture_is_sampled_within_the_chapters_ripped()
    {
        Assert.Equal(7, DvdRipper.SampleChapter([7]));
        Assert.Equal(8, DvdRipper.SampleChapter([7, 8, 9, 10]));
        Assert.Equal(2, DvdRipper.SampleChapter([2, 11, 12])); // a third of three: the first, as for a whole title
    }

    [Fact]
    public void Parts_to_join_keep_lpcm_as_pcm_and_the_join_makes_it_flac()
    {
        var o = Options([1, 3]);
        var part = DvdRipper.MkvArguments(o, preindex: true, lpcm: [0, 2], "T", "/p.mkv", range: new ChapterRun(1, 1), partBits: [16, 24]).ToList();
        Assert.Equal(["-c:a:0", "pcm_s16le", "-c:a:2", "pcm_s24le"], part.SkipWhile(a => a != "-c:a:0").Take(4));
        Assert.DoesNotContain("flac", part);
        var join = DvdRipper.JoinArguments("/parts.txt", "/chapters.txt", "/out/T.mkv", [0, 2]).ToList();
        Assert.Equal(["-f", "concat", "-safe", "0", "-i", "/parts.txt", "-i", "/chapters.txt", "-map", "0", "-map_metadata", "1",
                "-map_chapters", "1", "-c", "copy", "-c:a:0", "flac", "-compression_level:a:0", "8", "-c:a:2", "flac", "-compression_level:a:2", "8",
                "-f", "matroska", "/out/T.mkv"],
            join.SkipWhile(a => a != "-f"));
    }

    [Fact]
    public void Joined_chapters_keep_their_marks_moved_along_and_named_after_the_disc()
    {
        var meta = DvdRipper.JoinedChapters("The One", [
            (new ChapterRun(1, 1), 710.68, []), // one chapter: no marks of its own
            (new ChapterRun(3, 4), 1056.4, [(0.5, 436.7), (436.7, 1056.4)]), // a run's marks, from its first frame's time
        ]);
        Assert.Equal("""
            ;FFMETADATA1
            title=The One
            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=710680
            title=Chapter 1
            [CHAPTER]
            TIMEBASE=1/1000
            START=710680
            END=1146880
            title=Chapter 3
            [CHAPTER]
            TIMEBASE=1/1000
            START=1146880
            END=1767080
            title=Chapter 4

            """.Replace("\r", ""), meta);
        Assert.Contains("title=A\\=B\\;C", DvdRipper.JoinedChapters("A=B;C", []));
    }

    [Fact]
    public void A_parts_length_and_marks_come_from_ffprobe()
    {
        var (duration, marks) = DvdRipper.ParseParts("""
            {"chapters": [{"start_time": "0.000000", "end_time": "612.920000"}, {"start_time": "612.920000", "end_time": "1049.120000"}],
             "format": {"duration": "1049.120000"}}
            """);
        Assert.Equal(1049.12, duration, 3);
        Assert.Equal([(0.0, 612.92), (612.92, 1049.12)], marks);
        Assert.Equal([16, 0], DvdRipper.ParseAudioBits("""
            {"streams": [{"codec_type": "video"}, {"codec_type": "audio", "codec_name": "pcm_dvd", "bits_per_raw_sample": "16"},
              {"codec_type": "audio", "codec_name": "ac3"}]}
            """));
    }

    /// <summary>A title set's IFO with one title of 3 chapters in one program chain of 4 cells (program 2 has two).</summary>
    internal static byte[] ChapterIfo(bool angleBlock = false)
    {
        var vts = new byte[3 * 2048];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(vts, 0);
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(0xC8), 1); // VTS_PTT_SRPT at sector 1
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(0xCC), 2); // VTS_PGCI at sector 2
        var ptt = 2048;
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(ptt), 1); // titles
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(ptt + 4), 12 + 3 * 4 - 1); // end address
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(ptt + 8), 12); // title 1's chapters start here
        for (var c = 0; c < 3; c++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(ptt + 12 + 4 * c), 1); // program chain 1
            BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(ptt + 12 + 4 * c + 2), (ushort)(c + 1)); // program c+1
        }
        var pgci = 2 * 2048;
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(pgci), 1);
        BinaryPrimitives.WriteUInt32BigEndian(vts.AsSpan(pgci + 8 + 4), 16); // chain 1 at +16
        var pgc = pgci + 16;
        vts[pgc + 2] = 3; // programs
        vts[pgc + 3] = (byte)(angleBlock ? 5 : 4); // cells
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(pgc + 0xE6), 0xEC); // program map
        BinaryPrimitives.WriteUInt16BigEndian(vts.AsSpan(pgc + 0xE8), 0xF0); // cell playback table
        byte[] map = angleBlock ? [1, 2, 5] : [1, 2, 4];
        map.CopyTo(vts, pgc + 0xEC);
        // Cells' times (BCD h, m, s, frames | 25 fps): 1:00, 0:30, 0:30, 2:00; with an angle block, cell 3's other angle (0:30) too.
        (byte M, byte S, byte Category)[] cells = angleBlock
            ? [(0x01, 0x00, 0), (0x00, 0x30, 0), (0x00, 0x30, 0x50), (0x00, 0x30, 0xD0), (0x02, 0x00, 0)]
            : [(0x01, 0x00, 0), (0x00, 0x30, 0), (0x00, 0x30, 0), (0x02, 0x00, 0)];
        for (var i = 0; i < cells.Length; i++)
        {
            var at = pgc + 0xF0 + 24 * i;
            vts[at] = cells[i].Category;
            vts[at + 5] = cells[i].M;
            vts[at + 6] = cells[i].S;
            vts[at + 7] = 0x40; // 25 fps, 0 frames
        }
        return vts;
    }

    [Fact]
    public void Each_chapters_length_is_its_cells()
    {
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)], DvdInfo.ChapterLengths(ChapterIfo(), 1));
        // A multi-angle block counts once: the first angle's cell.
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)], DvdInfo.ChapterLengths(ChapterIfo(angleBlock: true), 1));
        Assert.Empty(DvdInfo.ChapterLengths(new byte[2048], 1));
        var title = new DvdTitle(1, 1, 3, 1, TimeSpan.FromMinutes(4), [], []) { ChapterLengths = DvdInfo.ChapterLengths(ChapterIfo(), 1) };
        Assert.Equal(TimeSpan.FromMinutes(2), title.ChapterLength(3));
        Assert.Null(title.ChapterLength(4));
    }

    [Fact]
    public void Both_encoders_deinterlace_with_bwdif_in_the_field_order_found()
    {
        foreach (var video in new[] { DvdVideo.X264, DvdVideo.X265 })
        {
            Assert.Equal("bwdif=mode=send_field:parity=tff:deint=all", DvdRipper.VideoArguments(video, VideoScan.TopFieldFirst).ElementAt(1));
            Assert.Equal("bwdif=mode=send_field:parity=bff:deint=all", DvdRipper.VideoArguments(video, VideoScan.BottomFieldFirst).ElementAt(1));
            Assert.DoesNotContain("-filter:v", DvdRipper.VideoArguments(video, VideoScan.Progressive));
        }
        Assert.DoesNotContain("+ilme+ildct", DvdRipper.VideoArguments(DvdVideo.X264, VideoScan.TopFieldFirst));
    }
}

/// <summary>The settings file, subtitle styling, the controls bar's choice and the MEGA app's links (1.12.0).</summary>
public class Prompt10SettingsTests
{
    [Fact]
    public void Subtitles_look_as_set()
    {
        var s = new PlayerSettings();
        var o = MpvOptionsBuilder.Subtitles(s).ToDictionary(k => k.Key, k => k.Value);
        Assert.Equal("sans-serif", o["sub-font"]);
        Assert.Equal("#FFFFFF", o["sub-color"]);
        Assert.Equal("2.5", o["sub-border-size"]);
        Assert.Equal("outline-and-shadow", o["sub-border-style"]);
        Assert.Equal("100", o["sub-pos"]);
        Assert.Equal("no", o["sub-ass-override"]);

        s.SubtitleFont = "DejaVu Serif";
        s.SubtitleBold = true;
        s.SubtitleColor = "#ffe600";
        s.SubtitleBackground = true;
        s.SubtitleBackgroundOpacity = 0.6;
        s.SubtitlePosition = 90;
        s.SubtitleShadow = 1.5;
        s.SubtitleStyleAss = true;
        s.Normalize();
        o = MpvOptionsBuilder.Subtitles(s).ToDictionary(k => k.Key, k => k.Value);
        Assert.Equal("DejaVu Serif", o["sub-font"]);
        Assert.Equal("yes", o["sub-bold"]);
        Assert.Equal("#FFE600", o["sub-color"]);
        Assert.Equal("background-box", o["sub-border-style"]);
        Assert.Equal("#99000000", o["sub-back-color"]); // mpv's #AARRGGBB
        Assert.Equal("0", o["sub-border-size"]); // the box instead of the outline
        Assert.Equal("1.5", o["sub-shadow-offset"]);
        Assert.Equal("90", o["sub-pos"]);
        Assert.Equal("force", o["sub-ass-override"]);
        Assert.Contains("sub-border-style", MpvOptionsBuilder.OptionalOptions); // mpv 0.37 (Ubuntu 24.04) lacks it
    }

    [Theory]
    [InlineData("#fff", "#FFFFFF")]
    [InlineData("ffe600", "#FFE600")]
    [InlineData("#12345", "#000000")]
    [InlineData("red", "#000000")]
    [InlineData(null, "#000000")]
    public void Colours_are_written_as_mpv_takes_them(string? typed, string expected) =>
        Assert.Equal(expected, PlayerSettings.NormalColor(typed, "#000000"));

    [Fact]
    public void Hand_edited_subtitle_and_bar_settings_are_tidied()
    {
        var s = new PlayerSettings
        {
            SubtitleColor = "nonsense", SubtitlePosition = 900, SubtitleBorderSize = -3, SubtitleBackgroundOpacity = 4,
            HiddenControls = ["stop", "frames", "stop", "no-such-button"],
        };
        s.Normalize();
        Assert.Equal(PlayerSettings.DefaultSubtitleColor, s.SubtitleColor);
        Assert.Equal(150, s.SubtitlePosition);
        Assert.Equal(0, s.SubtitleBorderSize);
        Assert.Equal(1, s.SubtitleBackgroundOpacity);
        Assert.Equal(["stop", "frames"], s.HiddenControls);
        s.SubtitleBold = true;
        s.ResetSubtitleStyle();
        Assert.False(s.SubtitleBold);
        Assert.Equal(PlayerSettings.DefaultSubtitleBorderSize, s.SubtitleBorderSize);
    }

    [Fact]
    public void An_exported_file_carries_the_settings_but_not_the_machines_own()
    {
        var dir = TestEnvironment.TempDir();
        var file = Path.Combine(dir, SettingsStore.ExportName);
        var old = new PlayerSettings { SeekSeconds = 30, SubtitleColor = "#FFE600", HiddenControls = ["download"], DisplayFps = 144, ExclusiveAudio = true };
        old.Normalize();
        SettingsStore.Export(old, file);
        Assert.True(SettingsStore.IsSettingsFile(file));

        var here = new PlayerSettings { DisplayFps = 60, ExclusiveAudio = false };
        here.CopyFrom(SettingsStore.ReadFile(file)!);
        Assert.Equal(30, here.SeekSeconds);
        Assert.Equal("#FFE600", here.SubtitleColor);
        Assert.Equal(["download"], here.HiddenControls);
        Assert.Equal(60, here.DisplayFps); // this machine's monitor
        Assert.False(here.ExclusiveAudio);
        here.HiddenControls.Add("stop");
        Assert.Equal(["download"], old.HiddenControls); // a copy, not the same list
    }

    [Fact]
    public void Other_json_files_arent_settings_and_reading_one_writes_nothing()
    {
        var dir = TestEnvironment.TempDir();
        var other = Path.Combine(dir, "package.json");
        File.WriteAllText(other, """{"name": "x", "version": "1"}""");
        var broken = Path.Combine(dir, "settings.json");
        File.WriteAllText(broken, """{"settingsVersion": 4, "volume": "loud"}""");
        Assert.False(SettingsStore.IsSettingsFile(other));
        Assert.Null(SettingsStore.ReadFile(other));
        Assert.True(SettingsStore.IsSettingsFile(broken)); // looks like one…
        Assert.Null(SettingsStore.ReadFile(broken)); // …but can't be read: nothing taken
        Assert.Equal(2, Directory.GetFiles(dir).Length); // no .corrupt copy next to someone else's file
        Assert.False(SettingsStore.IsSettingsFile(Path.Combine(dir, "missing.json")));
        Assert.False(SettingsStore.IsSettingsFile(Media("sdr_h264.mp4")));
    }

    private static string Media(string name) => TestEnvironment.Media(name);

    [Theory]
    [InlineData("http://127.0.0.1:4443/HEpEyLrD/Michael%20%282026%29.mkv", InputKind.MegaApp)]
    [InlineData("http://localhost:4443/abc/clip.mp4", InputKind.MegaApp)]
    [InlineData("http://127.0.0.1:4443/onlyone", InputKind.Url)]
    [InlineData("http://127.0.0.1:8080/abc/clip.mp4", InputKind.Url)]
    [InlineData("https://example.com:4443/abc/clip.mp4", InputKind.Url)]
    public void The_mega_apps_links_are_told_apart(string link, InputKind kind)
    {
        Assert.Equal(kind, InputClassifier.Classify(link).Kind);
        Assert.True(InputClassifier.IsStreaming(kind));
    }

    [Fact]
    public void Only_play_and_settings_cant_be_hidden()
    {
        Assert.DoesNotContain("play", PlayerSettings.ControlIds);
        Assert.DoesNotContain("settings", PlayerSettings.ControlIds);
        Assert.Equal(PlayerSettings.ControlIds.Count, PlayerSettings.ControlIds.Distinct().Count());
    }

    [Fact]
    public void Mpv_falls_back_from_vulkan_to_egl_on_linux()
    {
        var o = MpvOptionsBuilder.Core(new PortablePaths(TestEnvironment.TempDir()), 42).ToDictionary(k => k.Key, k => k.Value);
        if (OperatingSystem.IsLinux()) Assert.Equal("x11vk,x11egl", o["gpu-context"]);
        else Assert.DoesNotContain("gpu-context", o.Keys);
        // mpv 0.37 takes no list: the first, alone.
        Assert.Equal("x11vk", MpvOptionsBuilder.OlderFallbacks["gpu-context"]);
    }
}

/// <summary>The Flatpak's graphics driver check (an NVIDIA driver without its extension draws on the CPU).</summary>
public class Prompt10GraphicsTests
{
    private const string Proprietary = "NVRM version: NVIDIA UNIX x86_64 Kernel Module  580.95.05  Tue Sep 23 09:16:37 UTC 2025\nGCC version:  gcc";
    private const string Open = "NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  590.44.01  Release Build  (dvs-builder@U16-I1-N08-12-2)";

    [Fact]
    public void The_drivers_version_is_read_from_proc()
    {
        Assert.Equal("580.95.05", GraphicsDriverCheck.NvidiaVersion(Proprietary));
        Assert.Equal("590.44.01", GraphicsDriverCheck.NvidiaVersion(Open));
        Assert.Null(GraphicsDriverCheck.NvidiaVersion("something else"));
        Assert.Null(GraphicsDriverCheck.NvidiaVersion(null));
    }

    private static GraphicsProblem? Check(string? flatpak, bool nvidia, bool extension, string proc = Open) =>
        GraphicsDriverCheck.MissingNvidiaExtension(flatpak,
            p => p == "/dev/nvidiactl" ? nvidia : p == $"{GraphicsDriverCheck.ExtensionsDir}/nvidia-590-44-01" && extension,
            p => p == "/proc/driver/nvidia/version" ? proc : null);

    [Fact]
    public void A_missing_extension_is_named_with_the_command_that_installs_it()
    {
        var problem = Check("io.github.raulblideran.Paramaja", nvidia: true, extension: false);
        Assert.NotNull(problem);
        Assert.Equal("flatpak install flathub org.freedesktop.Platform.GL.nvidia-590-44-01", problem.Command);
        Assert.Contains("590.44.01", problem.Summary);
        Assert.Null(Check("io.github.raulblideran.Paramaja", nvidia: true, extension: true));
        Assert.Null(Check("io.github.raulblideran.Paramaja", nvidia: false, extension: false)); // AMD, Intel: Mesa comes with the runtime
        Assert.Null(Check(null, nvidia: true, extension: false)); // not the Flatpak: the system's own driver
        Assert.Null(Check("io.github.raulblideran.Paramaja", nvidia: true, extension: false, proc: "unreadable"));
    }
}

/// <summary>Previous / Next through a folder: mpv's playlist, with real libmpv.</summary>
public class Prompt10PlaylistTests
{
    [MpvFact]
    public void A_folders_files_are_a_playlist_to_step_through()
    {
        var dir = TestEnvironment.TempDir();
        foreach (var n in new[] { "a", "b", "c" }) File.Copy(TestEnvironment.Media("sdr_h264.mp4"), Path.Combine(dir, $"{n}.mp4"));
        using var h = new PlayerHarness();
        h.OpenAndWait(dir);
        PlayerHarness.WaitUntil(() => h.Player.State.PlaylistCount == 3, "the folder's three files");
        Assert.Equal(0, h.Player.State.PlaylistPos);
        h.Player.PlaylistStep(1);
        PlayerHarness.WaitUntil(() => h.Player.State.PlaylistPos == 1, "the next file");
        h.Player.PlaylistStep(-1);
        PlayerHarness.WaitUntil(() => h.Player.State.PlaylistPos == 0, "the previous file");
        h.Player.PlaylistStep(-1); // nothing before the first
        Thread.Sleep(300);
        Assert.Equal(0, h.Player.State.PlaylistPos);
    }

    [MpvFact]
    public void A_single_file_is_a_playlist_of_one()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"));
        PlayerHarness.WaitUntil(() => h.Player.State.PlaylistCount == 1, "one entry");
    }
}
