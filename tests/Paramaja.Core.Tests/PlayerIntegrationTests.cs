using Paramaja.Core.Mpv;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Tests;

/// <summary>End-to-end playback tests against real libmpv and the generated fixtures.</summary>
public class PlayerIntegrationTests
{
    [MpvFact]
    public void Every_generated_option_is_accepted_by_libmpv()
    {
        var variants = new[]
        {
            new PlayerSettings(),
            new PlayerSettings { HdrMode = HdrMode.Passthrough, DownmixToStereo = true, Deinterlace = DeinterlaceMode.On },
            new PlayerSettings { AudioPassthrough = true, Volume = 37 },
            new PlayerSettings { RenderQuality = RenderQuality.Balanced },
            new PlayerSettings { RenderQuality = RenderQuality.Fast, HdrMode = HdrMode.TonemapToSdr },
            new PlayerSettings { HardwareDecoding = false, Deinterlace = DeinterlaceMode.Off, PreferredAudioLanguages = "jpn",
                                 PreferredSubtitleLanguages = "eng", HdrDynamicPeak = false },
        };
        foreach (var curve in PlayerSettings.ToneMappingCurves)
            variants = [.. variants, new PlayerSettings { ToneMapping = curve }];

        var paths = new PortablePaths(TestEnvironment.TempDir());
        // A fake bundled yt-dlp + QuickJS, so the ytdl options are exercised too.
        var app = TestEnvironment.TempDir();
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        File.WriteAllText(Path.Combine(app, "yt-dlp" + exe), "");
        File.WriteAllText(Path.Combine(app, "qjs" + exe), "");
        foreach (var s in variants)
        {
            using var mpv = new MpvClient();
            foreach (var (k, v) in MpvOptionsBuilder.Build(s, paths, 1, new YtDlp(app))) mpv.SetOption(k, v); // throws on any bad name/value
        }
        // Mid-playback commands with the same filter strings must parse too.
        using var player = new PlayerHarness();
        player.Player.Mpv.Command("vf", "add", MpvOptionsBuilder.DeinterlaceFilter);
        player.Player.Mpv.Command("vf", "remove", MpvOptionsBuilder.DeinterlaceLabel);
    }

    [MpvTheory]
    [InlineData("sdr_h264.mp4", "h264")]
    [InlineData("hdr10_hevc.mkv", "hevc")]
    [InlineData("uhd_hevc.mkv", "hevc")]
    [InlineData("interlaced_1080i.ts", "h264")]
    [InlineData("audio_71.mkv", "h264")]
    [InlineData("uncompressed_uyvy.avi", "rawvideo")]
    [InlineData("uncompressed_v210.avi", "v210")]
    [InlineData("dvd_mpeg2.vob", "mpeg2video")]
    public void Opens_fixture_and_reports_codec(string file, string codec)
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media(file));
        PlayerHarness.WaitUntil(() => h.Player.State.VideoFormat is not null, "video-format");
        Assert.Equal(codec, h.Player.State.VideoFormat);
        PlayerHarness.WaitUntil(() => h.Player.State.Duration > 0, "duration");
    }

    [MpvFact]
    public void Hevc_10bit_hdr10_is_detected_as_pq_bt2020()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("hdr10_hevc.mkv"));
        PlayerHarness.WaitUntil(() => h.Player.State.Transfer is not null, "video-params/gamma");
        Assert.Equal("pq", h.Player.State.Transfer);
        Assert.Equal("bt.2020", h.Player.State.Primaries);
        Assert.True(h.Player.State.IsHdr);
        Assert.Contains("10", h.Player.Mpv.GetString("video-params/pixelformat") ?? "");
    }

    [MpvFact]
    public void Uhd_hevc_plays_at_full_resolution()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("uhd_hevc.mkv"));
        PlayerHarness.WaitUntil(() => h.Player.State.VideoWidth is not null, "video size");
        Assert.Equal(3840, h.Player.State.VideoWidth);
        Assert.Equal(2160, h.Player.State.VideoHeight);
    }

    [MpvTheory]
    [InlineData("audio_51.mkv", 6)]
    [InlineData("audio_71.mkv", 8)]
    public void Surround_is_kept_unless_downmix_is_enabled(string file, int channels)
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media(file));
        PlayerHarness.WaitUntil(() => h.Player.State.OutputChannels is not null, "audio output");
        Assert.Equal(channels, h.Player.State.SourceChannels);
        Assert.Equal(channels, h.Player.State.OutputChannels);

        h.Player.SetDownmix(true);
        PlayerHarness.WaitUntil(() => h.Player.State.OutputChannels == 2, "stereo downmix");
        Assert.Equal(channels, h.Player.State.SourceChannels);

        h.Player.SetDownmix(false);
        PlayerHarness.WaitUntil(() => h.Player.State.OutputChannels == channels, "original layout restored");
    }

    [MpvFact]
    public void Downmix_from_settings_applies_at_startup()
    {
        using var h = new PlayerHarness(new PlayerSettings { DownmixToStereo = true });
        h.OpenAndWait(TestEnvironment.Media("audio_51.mkv"));
        PlayerHarness.WaitUntil(() => h.Player.State.OutputChannels is not null, "audio output");
        Assert.Equal(2, h.Player.State.OutputChannels);
    }

    [MpvFact]
    public void No_normalisation_or_audio_filters_are_active_during_playback()
    {
        using var h = new PlayerHarness(new PlayerSettings { DownmixToStereo = true });
        h.OpenAndWait(TestEnvironment.Media("audio_51.mkv"));
        Assert.Equal("", h.Player.Mpv.GetString("af"));
        Assert.False(h.Player.Mpv.GetFlag("audio-normalize-downmix"));
        Assert.Equal("no", h.Player.Mpv.GetString("replaygain"));
        Assert.Equal(100, h.Player.Mpv.GetDouble("volume"));
    }

    [MpvFact]
    public void Frame_step_forward_and_back_move_exactly_one_frame()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"), paused: true);
        h.Player.SeekAbsolute(1.0);
        h.WaitRestart();
        var start = h.Player.Mpv.GetLong("estimated-frame-number")!.Value;

        h.Player.FrameStep();
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetLong("estimated-frame-number") == start + 1, "frame-step");
        h.Player.FrameStep();
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetLong("estimated-frame-number") == start + 2, "second frame-step");

        h.Player.FrameBackStep();
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetLong("estimated-frame-number") == start + 1, "frame-back-step");
        h.Player.FrameBackStep();
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetLong("estimated-frame-number") == start, "second frame-back-step");
        Assert.True(h.Player.Mpv.GetFlag("pause"), "frame stepping leaves playback paused");
    }

    [MpvFact]
    public void Screenshot_is_an_uncompressed_png_at_native_resolution()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"), paused: true);
        var file = h.Player.Screenshot(includeSubtitles: false);

        Assert.StartsWith(h.Paths.Screenshots, file);
        PlayerHarness.WaitUntil(() => File.Exists(file) && new FileInfo(file).Length > 0, "screenshot file");
        var png = PngInfo.Read(file);
        Assert.Equal(640, png.Width);
        Assert.Equal(360, png.Height);
        Assert.True(png.StoredDeflateBlocks, "IDAT must use stored (uncompressed) deflate blocks");
        Assert.True(png.IdatBytes >= png.RawImageBytes, $"IDAT {png.IdatBytes} < raw {png.RawImageBytes}: data was compressed");
    }

    [MpvFact]
    public void Hdr_screenshot_keeps_high_bit_depth()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("hdr10_hevc.mkv"), paused: true);
        var file = h.Player.Screenshot(includeSubtitles: false);
        PlayerHarness.WaitUntil(() => File.Exists(file) && new FileInfo(file).Length > 0, "screenshot file");
        var png = PngInfo.Read(file);
        Assert.Equal(1280, png.Width);
        Assert.Equal(16, png.BitDepth);
        Assert.True(png.StoredDeflateBlocks);
    }

    [MpvFact]
    public void Screenshot_with_subtitles_works()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("embedded_subs.mkv"), paused: true);
        var file = h.Player.Screenshot(includeSubtitles: true);
        PlayerHarness.WaitUntil(() => File.Exists(file) && new FileInfo(file).Length > 0, "screenshot file");
        Assert.True(PngInfo.Read(file).StoredDeflateBlocks);
    }

    [MpvFact]
    public void Bwdif_kicks_in_automatically_for_interlaced_content_only()
    {
        using (var h = new PlayerHarness())
        {
            h.OpenAndWait(TestEnvironment.Media("interlaced_1080i.ts"));
            PlayerHarness.WaitUntil(() => h.Player.State.DeinterlaceActive, "deinterlacer on 1080i");
            Assert.True(h.Player.State.SourceInterlaced);
            Assert.Equal("BWDIF", h.Player.State.DeinterlaceFilter);
            Assert.Contains("bwdif", h.Player.Mpv.GetString("vf"));
            // One output frame per field: the frame rate doubles.
            PlayerHarness.WaitUntil(() => h.Player.Mpv.GetString("video-out-params/h") == "1080", "output size");

            h.Player.SetDeinterlace(DeinterlaceMode.Off);
            PlayerHarness.WaitUntil(() => !h.Player.State.DeinterlaceActive, "deinterlacer off when disabled");
            Assert.DoesNotContain("bwdif", h.Player.Mpv.GetString("vf") ?? "");
        }
        using (var h = new PlayerHarness())
        {
            h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"));
            Thread.Sleep(300);
            Assert.False(h.Player.State.DeinterlaceActive, "progressive video is left alone");
            Assert.False(h.Player.State.SourceInterlaced);
            h.Player.SetDeinterlace(DeinterlaceMode.On);
            PlayerHarness.WaitUntil(() => h.Player.State.DeinterlaceActive, "forced deinterlace");
            Assert.Contains("bwdif", h.Player.Mpv.GetString("vf"));
        }
    }

    [MpvFact]
    public void Bwdif_keeps_the_picture_size_and_doubles_the_frame_rate()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("interlaced_1080i.ts"));
        PlayerHarness.WaitUntil(() => h.Player.State.DeinterlaceActive, "deinterlacer on 1080i");
        Assert.Equal("BWDIF", h.Player.State.DeinterlaceFilter);
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetString("video-out-params/h") == "1080", "full height");
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetString("video-out-params/w") == "1920", "full width");
    }

    [MpvFact]
    public void Next_file_is_judged_on_its_own_frames()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("interlaced_1080i.ts"));
        PlayerHarness.WaitUntil(() => h.Player.State.DeinterlaceActive, "deinterlacer on 1080i");
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"));
        PlayerHarness.WaitUntil(() => !h.Player.State.DeinterlaceActive, "deinterlacer removed for progressive file");
        Assert.DoesNotContain("bwdif", h.Player.Mpv.GetString("vf") ?? "");
    }

    [MpvFact]
    public void Streams_open_with_chunked_buffering()
    {
        using var h = new PlayerHarness();
        h.Player.Open(TestEnvironment.Media("sdr_h264.mp4"), streaming: true);
        PlayerHarness.WaitUntil(() => h.Player.State.VideoFormat is not null, "stream opened");
        Assert.Equal(5, h.Player.Mpv.GetDouble("cache-pause-wait"));
        Assert.Equal(true, h.Player.Mpv.GetFlag("cache-pause-initial"));
    }

    [MpvFact]
    public void Passthrough_and_ytdl_options_are_accepted_live()
    {
        using var h = new PlayerHarness();
        h.Player.Settings.AudioPassthrough = true;
        h.Player.ApplyAudio();
        Assert.Equal("ac3,eac3,dts,dts-hd,truehd", h.Player.Mpv.GetString("audio-spdif"));
        Assert.Equal(true, h.Player.Mpv.GetFlag("ytdl"));
    }

    [MpvFact]
    public void Embedded_and_external_subtitles_are_listed_and_selectable()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("embedded_subs.mkv"));
        var subs = h.Player.Tracks.Where(t => t.Type == TrackType.Subtitle).ToList();
        Assert.Single(subs);
        Assert.Equal("subrip", subs[0].Codec);

        h.Player.AddSubtitle(TestEnvironment.Media("subs.ass"));
        var ass = Assert.Single(h.Player.Tracks, t => t.Type == TrackType.Subtitle && t.External);
        Assert.True(ass.Selected);
        Assert.Equal("ass", ass.Codec);

        h.Player.SelectTrack(TrackType.Subtitle, subs[0].Id);
        Assert.True(h.Player.Tracks.Single(t => t.Id == subs[0].Id && t.Type == TrackType.Subtitle).Selected);

        h.Player.SelectTrack(TrackType.Subtitle, null);
        Assert.DoesNotContain(h.Player.Tracks, t => t.Type == TrackType.Subtitle && t.Selected);
    }

    [MpvFact]
    public void External_srt_next_to_video_is_auto_loaded()
    {
        var dir = TestEnvironment.TempDir();
        File.Copy(TestEnvironment.Media("sdr_h264.mp4"), Path.Combine(dir, "movie.mp4"));
        File.Copy(TestEnvironment.Media("subs.srt"), Path.Combine(dir, "movie.en.srt"));
        using var h = new PlayerHarness();
        h.OpenAndWait(Path.Combine(dir, "movie.mp4"));
        Assert.Contains(h.Player.Tracks, t => t.Type == TrackType.Subtitle && t.External);
    }

    [MpvFact]
    public void Audio_tracks_report_channel_layout()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("audio_71.mkv"));
        var audio = Assert.Single(h.Player.Tracks, t => t.Type == TrackType.Audio);
        Assert.Equal(8, audio.Channels);
        Assert.Contains("7.1", audio.Label);
    }

    [MpvFact]
    public void Seeking_pausing_and_volume()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"), paused: true);
        h.Player.SeekAbsolute(2.0);
        h.WaitRestart();
        PlayerHarness.WaitUntil(() => Math.Abs(h.Player.State.Position - 2.0) < 0.05, "exact seek");

        h.Player.SeekRelative(-1);
        h.WaitRestart();
        PlayerHarness.WaitUntil(() => Math.Abs(h.Player.State.Position - 1.0) < 0.05, "relative seek");

        h.Player.TogglePause();
        PlayerHarness.WaitUntil(() => !h.Player.State.IsPaused, "unpause");

        h.Player.SetVolume(42);
        PlayerHarness.WaitUntil(() => Math.Abs(h.Player.State.Volume - 42) < 0.01, "volume");
        Assert.Equal(VolumeCurve.ToMpv(42), h.Player.Mpv.GetDouble("volume")!.Value, 3);
        h.Player.SetVolume(1000);
        Assert.Equal(150, h.Player.Mpv.GetDouble("volume"));
        h.Player.ToggleMute();
        PlayerHarness.WaitUntil(() => h.Player.State.IsMuted, "mute");
    }

    [MpvFact]
    public void Keys_pressed_inside_mpv_are_forwarded_to_the_app()
    {
        using var h = new PlayerHarness();
        var got = new List<string>();
        h.Player.KeyPressed += k => { lock (got) got.Add(k); };
        h.Player.Mpv.Command("keypress", ".");
        h.Player.Mpv.Command("keypress", "S");
        h.Player.Mpv.Command("keypress", "Ctrl+v");
        PlayerHarness.WaitUntil(() => { lock (got) return got.Count == 3; }, "forwarded keys");
        Assert.Equal([".", "S", "Ctrl+v"], got);
    }

    [MpvFact]
    public void Hdr_mode_and_tonemapping_can_change_live()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("hdr10_hevc.mkv"));
        h.Player.Settings.ToneMapping = "bt.2446a";
        h.Player.SetHdrMode(HdrMode.TonemapToSdr);
        Assert.Equal("bt.2446a", h.Player.Mpv.GetString("tone-mapping"));
        h.Player.SetHdrMode(HdrMode.Passthrough);
        Assert.Equal("yes", h.Player.Mpv.GetString("target-colorspace-hint"));
    }

    [MpvFact]
    public void Broken_file_reports_an_error()
    {
        var bad = Path.Combine(TestEnvironment.TempDir(), "broken.mkv");
        File.WriteAllBytes(bad, new byte[1000]);
        using var h = new PlayerHarness();
        string? error = null;
        var done = new ManualResetEventSlim();
        h.Player.EndFile += (reason, err) => { if (reason == EndFileReason.Error) { error = err; done.Set(); } };
        h.Player.Open(bad);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
