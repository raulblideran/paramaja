using NegsPlayer.Core.Playback;
using NegsPlayer.Core.Settings;

namespace NegsPlayer.Core.Tests;

public class OptionsBuilderTests
{
    private static readonly PortablePaths Paths = new(Path.Combine(Path.GetTempPath(), "negs-portable"));

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

    [Theory]
    [InlineData("bt.2446a")]
    [InlineData("bt.2390")]
    [InlineData("hable")]
    public void Tonemapping_curve_is_configurable(string curve) =>
        Assert.Equal(curve, Build(new PlayerSettings { ToneMapping = curve })["tone-mapping"]);

    [Theory]
    [InlineData(DeinterlaceMode.Auto, "auto")]
    [InlineData(DeinterlaceMode.On, "yes")]
    [InlineData(DeinterlaceMode.Off, "no")]
    public void Deinterlace_modes(DeinterlaceMode mode, string expected) =>
        Assert.Equal(expected, Build(new PlayerSettings { Deinterlace = mode })["deinterlace"]);

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
        Assert.Equal("no", o["ytdl"]);
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
