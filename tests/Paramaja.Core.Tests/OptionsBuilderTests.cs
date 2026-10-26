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
        var o = Build(new PlayerSettings());
        Assert.Equal("gpu-next", o["vo"]);
        Assert.Equal("no", o["target-colorspace-hint"]);
        Assert.Equal("spline", o["tone-mapping"]);
        Assert.Equal("yes", o["hdr-compute-peak"]);
        Assert.Equal("perceptual", o["gamut-mapping-mode"]);
        Assert.Equal("0.3", o["hdr-contrast-recovery"]);
    }

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
        Assert.Equal("spline", sdr["tone-mapping"]);
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

    [Theory]
    [InlineData(DeinterlaceMode.Auto)]
    [InlineData(DeinterlaceMode.On)]
    [InlineData(DeinterlaceMode.Off)]
    public void Mpvs_own_deinterlacer_is_never_used(DeinterlaceMode mode) =>
        // The app inserts BWDIF itself; mpv's option would pick the GPU deinterlacer.
        Assert.Equal("no", Build(new PlayerSettings { Deinterlace = mode })["deinterlace"]);

    [Fact]
    public void Deinterlacer_is_field_rate_bwdif() =>
        Assert.Equal("@paramaja-deint:bwdif=mode=send_field", MpvOptionsBuilder.DeinterlaceFilter);

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

        Assert.Equal(new Dictionary<string, string> { ["ytdl"] = "yes" }, Opts());

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
    public void Hardware_decoding_toggle()
    {
        Assert.Equal("auto-safe", Build(new PlayerSettings())["hwdec"]);
        Assert.Equal("no", Build(new PlayerSettings { HardwareDecoding = false })["hwdec"]);
    }

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
            var o = Build(new PlayerSettings { SubtitleScale = 1.5, HdrContrastRecovery = 0.25 });
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
