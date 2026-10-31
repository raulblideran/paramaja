using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Tests;

public class OptionsBuilderTests
{
    private static readonly PortablePaths Paths = new(Path.Combine(Path.GetTempPath(), "paramaja-portable"));

    private static Dictionary<string, string> Build(PlayerSettings s, long? wid = null)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in MpvOptionsBuilder.Build(s, Paths, wid)) d[k] = v; // last wins, like mpv
        return d;
    }

    [Fact]
    public void Audio_is_never_normalised()
    {
        var o = Build(new PlayerSettings());
        Assert.Equal("no", o["audio-normalize-downmix"]);
        Assert.Equal("no", o["replaygain"]);
        Assert.Equal("", o["af"]);
        Assert.DoesNotContain(o.Values, v => v.Contains("loudnorm") || v.Contains("dynaudnorm") || v.Contains("acompressor"));
    }

    [Theory]
    [InlineData("dsd_lsbf_planar", 192_000)]
    [InlineData("dsd_msbf", 192_000)]
    [InlineData("dst", 192_000)]
    [InlineData("flac", null)]
    [InlineData("pcm_s24le", null)]
    [InlineData(null, null)]
    public void Dsd_plays_at_192_khz_and_the_rest_at_its_own_rate(string? codec, int? rate)
    {
        Assert.Equal(rate, MpvOptionsBuilder.SampleRateFor(codec));
        Assert.DoesNotContain("audio-samplerate", Build(new PlayerSettings()).Keys);
    }

    [Fact]
    public void Exclusive_audio_is_off_by_default_and_on_for_passthrough_or_the_switch()
    {
        Assert.False(MpvOptionsBuilder.AudioExclusive(new PlayerSettings()));
        Assert.True(MpvOptionsBuilder.AudioExclusive(new PlayerSettings { ExclusiveAudio = true }));
        Assert.True(MpvOptionsBuilder.AudioExclusive(new PlayerSettings { AudioPassthrough = true }));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("no", Build(new PlayerSettings())["audio-exclusive"]);
            Assert.Equal("yes", Build(new PlayerSettings { ExclusiveAudio = true })["audio-exclusive"]);
        }
    }

    [Fact]
    public void Downmix_is_off_by_default_and_opt_in()
    {
        Assert.Equal("auto", Build(new PlayerSettings())["audio-channels"]);
        Assert.Equal("stereo", Build(new PlayerSettings { DownmixToStereo = true })["audio-channels"]);
        // Downmixing must still not normalise.
        Assert.Equal("no", Build(new PlayerSettings { DownmixToStereo = true })["audio-normalize-downmix"]);
    }

    [Fact]
    public void Hdr_tonemaps_to_sdr_by_default_with_gpu_next()
    {
        // High is the profile with every HDR feature on; Balanced (the default) drops recovery.
        var o = Build(new PlayerSettings { RenderQuality = RenderQuality.High });
        Assert.Equal("gpu-next", o["vo"]);
        Assert.Equal("no", o["target-colorspace-hint"]);
        Assert.Equal("mobius", o["tone-mapping"]);
        Assert.Equal("yes", o["hdr-compute-peak"]);
        Assert.Equal("perceptual", o["gamut-mapping-mode"]);
        Assert.Equal("0.3", o["hdr-contrast-recovery"]);
    }

    [Fact]
    public void Rendering_is_balanced_by_default()
    {
        Assert.Equal(RenderQuality.Balanced, new PlayerSettings().RenderQuality);
        Assert.Equal("0", Build(new PlayerSettings())["hdr-contrast-recovery"]);
    }

    [Theory]
    [InlineData("hevc", 1280, 720, true)]    // HEVC at any size
    [InlineData("hevc", 640, 360, true)]
    [InlineData("h264", 1920, 1080, true)]   // any codec from 1080p up
    [InlineData("av1", 3840, 2160, true)]
    [InlineData("h264", 1920, 800, true)]    // scope 1080p
    [InlineData("av1", 854, 480, true)]      // AV1 at any size, like HEVC
    [InlineData("h264", 1280, 720, false)]   // smaller H.264, VP9, MPEG-2…: software
    [InlineData("vp9", 854, 480, false)]
    [InlineData("mpeg2video", 720, 576, false)]
    [InlineData(null, null, null, false)]
    public void Hardware_decoding_is_for_hevc_av1_and_1080p_up(string? codec, int? w, int? h, bool hardware) =>
        Assert.Equal(hardware, MpvOptionsBuilder.UseHardwareDecoding(codec, w, h));

    [Fact]
    public void Frames_are_timed_by_the_audio_clock_unless_synced_to_the_display()
    {
        var o = Build(new PlayerSettings());
        Assert.Equal("audio", o["video-sync"]);
        Assert.Equal("0", o["display-fps-override"]); // mpv's own reading of the monitor
        foreach (var q in Enum.GetValues<RenderQuality>())
            Assert.Equal("audio", Build(new PlayerSettings { RenderQuality = q })["video-sync"]);

        var synced = Build(new PlayerSettings { SyncToDisplay = true });
        Assert.Equal("display-resample", synced["video-sync"]);
        Assert.Equal("0", synced["display-fps-override"]);
        Assert.Equal("60", Build(new PlayerSettings { SyncToDisplay = true, DisplayFps = 60 })["display-fps-override"]);
        // The rate only matters to display sync.
        Assert.Equal("0", Build(new PlayerSettings { DisplayFps = 60 })["display-fps-override"]);
    }

    [Theory]
    [InlineData(false, false, 6, "auto")]
    [InlineData(true, false, 6, "stereo")]   // stereo downmix: surround only
    [InlineData(true, false, 8, "stereo")]
    [InlineData(false, false, 8, "auto")]   // 7.1 is never downmixed unless asked
    [InlineData(true, false, 2, "auto")]
    [InlineData(false, false, 2, "auto")]   // nothing downmixes stereo any more (mono is gone)
    [InlineData(true, true, 6, "auto")]       // passthrough leaves the bitstream alone
    public void The_downmix_applies_only_to_surround(bool stereo, bool passthrough, int channels, string expected) =>
        Assert.Equal(expected, MpvOptionsBuilder.AudioChannels(
            new PlayerSettings { DownmixToStereo = stereo, AudioPassthrough = passthrough }, channels));

    [Theory]
    [InlineData(StereoChannel.Both, 2, false, false, "")]
    [InlineData(StereoChannel.Left, 2, false, false, "lavfi=[pan=stereo|c0=c0|c1=c0]")]
    [InlineData(StereoChannel.Right, 2, false, false, "lavfi=[pan=stereo|c0=c1|c1=c1]")]
    [InlineData(StereoChannel.Left, 6, false, false, "")]   // stereo sources only
    [InlineData(StereoChannel.Left, 1, false, false, "")]
    [InlineData(StereoChannel.Left, null, false, false, "")]
    [InlineData(StereoChannel.Right, 2, true, false, "lavfi=[pan=stereo|c0=c1|c1=c1]," + MpvOptionsBuilder.NormalizeFilter)]
    [InlineData(StereoChannel.Left, 2, true, true, "")]     // a bitstream can't be filtered
    public void One_channel_of_a_stereo_source_plays_on_both_speakers(StereoChannel channel, int? source, bool normalize,
        bool passthrough, string expected) =>
        Assert.Equal(expected, MpvOptionsBuilder.AudioFilters(
            new PlayerSettings { NormalizeAudio = normalize, AudioPassthrough = passthrough }, source, channel));

    private static TrackInfo Audio(int id, int? channels, string? lang = null, bool isDefault = false, bool external = false) =>
        new(id, TrackType.Audio, null, lang, "ac3", false, external, isDefault, channels);

    [Fact]
    public void A_stereo_track_plays_where_a_file_has_several()
    {
        Assert.Equal(2, MpvOptionsBuilder.StereoTrack([Audio(1, 6, isDefault: true), Audio(2, 2), Audio(3, 6)]));
        // Of two stereo tracks, the default one, else the first.
        Assert.Equal(3, MpvOptionsBuilder.StereoTrack([Audio(1, 6), Audio(2, 2), Audio(3, 2, isDefault: true)]));
        Assert.Equal(2, MpvOptionsBuilder.StereoTrack([Audio(1, 6), Audio(2, 2), Audio(3, 2)]));
        // Nothing to choose: one track, no stereo one, or only an external one.
        Assert.Null(MpvOptionsBuilder.StereoTrack([Audio(1, 2)]));
        Assert.Null(MpvOptionsBuilder.StereoTrack([Audio(1, 6), Audio(2, 8)]));
        Assert.Null(MpvOptionsBuilder.StereoTrack([Audio(1, 6), Audio(2, 2, external: true)]));
        Assert.Null(MpvOptionsBuilder.StereoTrack([Audio(1, null), Audio(2, null)]));
        Assert.Null(MpvOptionsBuilder.StereoTrack([new TrackInfo(1, TrackType.Video, null, null, "h264", true, false, true, null), Audio(2, 6)]));
    }

    [Fact]
    public void A_preferred_language_comes_before_stereo()
    {
        TrackInfo[] tracks = [Audio(1, 6, "jpn"), Audio(2, 2, "eng"), Audio(3, 2, "jpn"), Audio(4, 6, "fra")];
        Assert.Equal(3, MpvOptionsBuilder.StereoTrack(tracks, "jpn,eng"));
        Assert.Equal(2, MpvOptionsBuilder.StereoTrack(tracks, "ENG"));
        // French has no stereo track: mpv's own choice (the French one) stands.
        Assert.Null(MpvOptionsBuilder.StereoTrack(tracks, "fra"));
        // A language the file doesn't have doesn't count.
        Assert.Equal(2, MpvOptionsBuilder.StereoTrack(tracks, "deu"));
    }

    [Fact]
    public void Normalization_is_off_by_default_and_never_on_a_bitstream()
    {
        Assert.Equal("", Build(new PlayerSettings())["af"]);
        Assert.Contains("dynaudnorm", Build(new PlayerSettings { NormalizeAudio = true })["af"]);
        Assert.Equal("", Build(new PlayerSettings { NormalizeAudio = true, AudioPassthrough = true })["af"]);
    }

    [Theory]
    [InlineData(0, "bv*+ba/b")]
    [InlineData(1080, "bv*[height<=1080]+ba/b[height<=1080]/bv*+ba/b")]
    public void Stream_quality_caps_the_resolution(int maxHeight, string format) =>
        Assert.Equal(format, Build(new PlayerSettings { StreamMaxHeight = maxHeight })["ytdl-format"]);

    [Fact]
    public void Hdr_passthrough_hints_the_display()
    {
        var o = Build(new PlayerSettings { HdrMode = HdrMode.Passthrough });
        Assert.Equal("yes", o["target-colorspace-hint"]);
        Assert.False(o.ContainsKey("hdr-compute-peak"));
    }

    [Fact]
    public void Auto_hdr_tonemaps_on_sdr_and_passes_through_on_an_hdr_display()
    {
        var s = new PlayerSettings();
        Assert.Equal(HdrMode.Auto, s.HdrMode);
        var sdr = MpvOptionsBuilder.Hdr(s, RenderProfile.High, displayIsHdr: false).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("no", sdr["target-colorspace-hint"]);
        Assert.Equal("mobius", sdr["tone-mapping"]);
        var hdr = MpvOptionsBuilder.Hdr(s, RenderProfile.High, displayIsHdr: true).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("yes", hdr["target-colorspace-hint"]);
        // A manual choice wins over the display.
        Assert.Equal(HdrMode.TonemapToSdr, new PlayerSettings { HdrMode = HdrMode.TonemapToSdr }.EffectiveHdrMode(true));
        Assert.Equal(HdrMode.Passthrough, new PlayerSettings { HdrMode = HdrMode.Passthrough }.EffectiveHdrMode(false));
    }

    [Theory]
    [InlineData("bt.2446a")]
    [InlineData("bt.2390")]
    [InlineData("hable")]
    public void Tonemapping_curve_is_configurable(string curve) =>
        Assert.Equal(curve, Build(new PlayerSettings { ToneMapping = curve })["tone-mapping"]);

    [Fact]
    public void Mpvs_own_deinterlacer_is_never_used() =>
        // The app inserts BWDIF itself; mpv's option would pick the GPU deinterlacer.
        Assert.Equal("no", Build(new PlayerSettings())["deinterlace"]);

    [Fact]
    public void Deinterlacer_is_field_rate_bwdif()
    {
        Assert.Equal("@paramaja-deint:bwdif=mode=send_field", MpvOptionsBuilder.DeinterlaceFilter);
        // Flagged frames carry their own field order; unflagged ones get the picture check's.
        Assert.Equal(MpvOptionsBuilder.DeinterlaceFilter, MpvOptionsBuilder.DeinterlaceFilterFor(null));
        Assert.Equal("@paramaja-deint:bwdif=mode=send_field:parity=tff", MpvOptionsBuilder.DeinterlaceFilterFor(FieldOrder.TopFirst));
        Assert.Equal("@paramaja-deint:bwdif=mode=send_field:parity=bff", MpvOptionsBuilder.DeinterlaceFilterFor(FieldOrder.BottomFirst));
    }

    [Theory]
    [InlineData(301, 0, 0, FieldOrder.TopFirst)]      // a tape capture (MJPEG, unflagged): Brit Awards 1996
    [InlineData(0, 280, 4, FieldOrder.BottomFirst)]
    [InlineData(150, 0, 0, FieldOrder.TopFirst)]
    [InlineData(52, 0, 249, null)]                    // progressive films misjudged here and there
    [InlineData(4, 59, 238, null)]
    [InlineData(76, 38, 36, null)]                    // busy synthetic progressive video
    [InlineData(59, 0, 0, null)]                      // too little to go on (a still, black frames)
    [InlineData(0, 0, 0, null)]
    public void The_picture_check_needs_a_clear_majority(long tff, long bff, long progressive, FieldOrder? expected) =>
        Assert.Equal(expected, InterlaceVerdict.FromIdet(tff, bff, progressive));

    [Theory]
    [InlineData(576, 25.0, true)]
    [InlineData(480, 29.97, true)]
    [InlineData(1080, 25.0, true)]
    [InlineData(526, 25.0, true)]
    [InlineData(240, 29.97, false)]   // a single field's height
    [InlineData(360, 30.0, false)]
    [InlineData(576, 50.0, false)]    // 50p and 60p are progressive by nature
    [InlineData(480, 59.94, false)]
    [InlineData(2160, 25.0, false)]
    [InlineData(null, 25.0, false)]
    [InlineData(576, null, false)]
    public void Only_sizes_and_rates_interlaced_video_has_are_checked(int? height, double? fps, bool checks) =>
        Assert.Equal(checks, InterlaceVerdict.Applies(height, fps));

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData(6.0, 0.6)]
    [InlineData(300.0, 30.0)]
    [InlineData(7200.0, 60.0)]        // a little way in, at most a minute
    public void The_picture_check_samples_a_little_way_in(double? duration, double start) =>
        Assert.Equal(start, InterlaceProbe.StartFor(duration), 3);

    [Theory]
    [InlineData("h264", "H.264")]
    [InlineData("hevc", "HEVC")]
    [InlineData("av1", "AV1")]
    [InlineData("mpeg2video", "MPEG-2")]
    [InlineData("vc1", "VC-1")]
    [InlineData("vp6f", "VP6")]
    [InlineData("rawvideo", "Uncompressed")]
    [InlineData("mjpeg", "MJPEG")]
    [InlineData("somethingnew", "SOMETHINGNEW")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Codecs_have_short_familiar_names(string? codec, string label) =>
        Assert.Equal(label, CodecLabels.Describe(codec));

    [Theory]
    [InlineData("mpgv", "mpeg2video")]   // what libVLC calls a DVD's video
    [InlineData("h264", "h264")]
    [InlineData("hevc", "hevc")]
    [InlineData("WVC1", "vc1")]
    [InlineData(null, null)]
    public void LibVLC_codec_names_map_to_ffmpeg_names(string? fourcc, string? codec) =>
        Assert.Equal(codec, CodecLabels.FromFourCC(fourcc));

    [Fact]
    public void Volume_option_uses_the_logarithmic_curve()
    {
        Assert.Equal("100", Build(new PlayerSettings())["volume"]);
        Assert.Equal("31.623", Build(new PlayerSettings { Volume = 50 })["volume"]);
        Assert.Equal("150", Build(new PlayerSettings { Volume = 150 })["volume"]);
    }

    [Fact]
    public void Passthrough_is_off_by_default_and_overrides_downmix()
    {
        var o = Build(new PlayerSettings());
        Assert.Equal("", o["audio-spdif"]);
        var pt = Build(new PlayerSettings { AudioPassthrough = true, DownmixToStereo = true });
        Assert.Equal("ac3,eac3,dts,dts-hd,truehd", pt["audio-spdif"]);
        Assert.Equal("auto", pt["audio-channels"]);
    }

    [Fact]
    public void Ytdl_uses_the_bundled_yt_dlp_and_quickjs()
    {
        var dir = TestEnvironment.TempDir();
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        Dictionary<string, string> Opts() =>
            MpvOptionsBuilder.Ytdl(new YtDlp(dir)).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(new Dictionary<string, string> { ["ytdl"] = "yes", ["ytdl-format"] = "bv*+ba/b" }, Opts());

        File.WriteAllText(Path.Combine(dir, "yt-dlp" + exe), "");
        File.WriteAllText(Path.Combine(dir, "qjs" + exe), "");
        var o = Opts();
        var ytdlp = Path.Combine(dir, "yt-dlp" + exe);
        Assert.Equal($"ytdl_hook-ytdl_path=%{System.Text.Encoding.UTF8.GetByteCount(ytdlp)}%{ytdlp}", o["script-opts"]);
        Assert.EndsWith($"quickjs:{Path.Combine(dir, "qjs" + exe)}", o["ytdl-raw-options"]);
    }

    [Theory]
    [InlineData("C:\\Films, old\\yt-dlp.exe", "%24%C:\\Films, old\\yt-dlp.exe")]
    [InlineData("/home/ä/yt-dlp", "%15%/home/ä/yt-dlp")]
    public void Paths_are_length_quoted_in_utf8_bytes(string path, string expected) =>
        Assert.Equal(expected, MpvOptionsBuilder.Quote(path));

    [Fact]
    public void Streams_buffer_in_bigger_chunks()
    {
        var opts = MpvOptionsBuilder.StreamingFileOptions.Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("yes", opts["cache-pause-initial"]);
        Assert.Equal("5", opts["cache-pause-wait"]);
        Assert.Equal("1024MiB", opts["demuxer-max-bytes"]);
    }

    [Fact]
    public void Hardware_decoding_is_decided_per_file_not_at_startup() =>
        // PlayerService switches it on per file, once codec and size are known.
        Assert.Equal("no", Build(new PlayerSettings())["hwdec"]);

    [Fact]
    public void Screenshots_are_uncompressed_png_in_the_portable_folder()
    {
        var o = Build(new PlayerSettings());
        Assert.Equal("png", o["screenshot-format"]);
        Assert.Equal("0", o["screenshot-png-compression"]);
        Assert.Equal("0", o["screenshot-png-filter"]);
        Assert.Equal("yes", o["screenshot-high-bit-depth"]);
        Assert.Equal(Paths.Screenshots, o["screenshot-directory"]);
    }

    [Fact]
    public void Mpv_config_stays_inside_the_app_folder()
    {
        var o = Build(new PlayerSettings());
        Assert.Equal(Paths.MpvConfig, o["config-dir"]);
        Assert.StartsWith(Paths.Root, o["config-dir"]);
        Assert.Equal("yes", o["ytdl"]);
        foreach (var key in new[] { "gpu-shader-cache-dir", "icc-cache-dir", "demuxer-cache-dir", "watch-later-dir", "screenshot-directory" })
            Assert.StartsWith(Paths.Root, o[key]);
    }

    [Fact]
    public void Window_id_is_passed_when_embedding()
    {
        Assert.False(Build(new PlayerSettings()).ContainsKey("wid"));
        var o = Build(new PlayerSettings(), wid: 12345);
        Assert.Equal("12345", o["wid"]);
        Assert.Equal("yes", o["force-window"]);
    }

    [Fact]
    public void Frame_exact_seeking_is_enabled()
    {
        var o = Build(new PlayerSettings());
        Assert.Equal("yes", o["hr-seek"]);
        Assert.Equal("no", o["input-default-bindings"]);
        Assert.Equal("no", o["osc"]);
    }

    [Fact]
    public void Numbers_use_invariant_culture()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var o = Build(new PlayerSettings { SubtitleScale = 1.5, HdrContrastRecovery = 0.25, RenderQuality = RenderQuality.High });
            Assert.Equal("1.5", o["sub-scale"]);
            Assert.Equal("0.25", o["hdr-contrast-recovery"]);
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }

    [Fact]
    public void Language_preferences_are_only_emitted_when_set()
    {
        Assert.False(Build(new PlayerSettings()).ContainsKey("alang"));
        var o = Build(new PlayerSettings { PreferredAudioLanguages = "jpn,eng", PreferredSubtitleLanguages = "eng" });
        Assert.Equal("jpn,eng", o["alang"]);
        Assert.Equal("eng", o["slang"]);
    }
}
