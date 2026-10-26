using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Tests;

public class RenderProfileTests
{
    private static Dictionary<string, string> Opts(IEnumerable<KeyValuePair<string, string>> o) =>
        o.ToDictionary(kv => kv.Key, kv => kv.Value);

    [Theory]
    [InlineData(3840, 2160, RenderProfile.Balanced)]
    [InlineData(3840, 1600, RenderProfile.Balanced)] // 4K scope
    [InlineData(4096, 1716, RenderProfile.Balanced)]
    [InlineData(1920, 1080, RenderProfile.High)]
    [InlineData(1920, 800, RenderProfile.High)]
    [InlineData(null, null, RenderProfile.High)]
    public void Auto_uses_balanced_for_4k_and_high_below(int? w, int? h, RenderProfile expected) =>
        Assert.Equal(expected, MpvOptionsBuilder.AutoProfileFor(w, h));

    [Fact]
    public void High_keeps_the_best_scalers()
    {
        var o = Opts(MpvOptionsBuilder.Scaling(RenderProfile.High));
        Assert.Equal("ewa_lanczossharp", o["scale"]);
        Assert.Equal("ewa_lanczossharp", o["cscale"]);
        Assert.Equal("display-resample", o["video-sync"]);
    }

    [Fact]
    public void Balanced_drops_the_expensive_ewa_chroma_scaler()
    {
        var o = Opts(MpvOptionsBuilder.Scaling(RenderProfile.Balanced));
        Assert.DoesNotContain("ewa", o["scale"] + o["cscale"] + o["dscale"]);
        Assert.Equal("no", o["correct-downscaling"]);
        var hdr = Opts(MpvOptionsBuilder.Hdr(new PlayerSettings(), RenderProfile.Balanced));
        Assert.Equal("0", hdr["hdr-contrast-recovery"]);
        Assert.Equal("yes", hdr["hdr-compute-peak"]);
    }

    [Fact]
    public void Fast_is_bilinear_with_static_tonemapping()
    {
        var o = Opts(MpvOptionsBuilder.Scaling(RenderProfile.Fast));
        Assert.Equal("bilinear", o["scale"]);
        Assert.Equal("bilinear", o["cscale"]);
        Assert.Equal("audio", o["video-sync"]);
        var hdr = Opts(MpvOptionsBuilder.Hdr(new PlayerSettings(), RenderProfile.Fast));
        Assert.Equal("no", hdr["hdr-compute-peak"]);
        Assert.Equal("0", hdr["hdr-contrast-recovery"]);
    }

    [Theory]
    [InlineData(RenderQuality.Auto, "ewa_lanczossharp")]
    [InlineData(RenderQuality.High, "ewa_lanczossharp")]
    [InlineData(RenderQuality.Balanced, "bilinear")]
    [InlineData(RenderQuality.Fast, "bilinear")]
    public void Startup_options_follow_the_setting(RenderQuality q, string cscale)
    {
        var paths = new PortablePaths(TestEnvironment.TempDir());
        var o = Opts(MpvOptionsBuilder.Build(new PlayerSettings { RenderQuality = q }, paths)
            .GroupBy(kv => kv.Key).Select(g => g.Last()));
        Assert.Equal(cscale, o["cscale"]);
    }

    [Fact]
    public void Stream_buffer_doubles_after_each_stall_up_to_a_minute() =>
        Assert.Equal([5.0, 10, 20, 40, 60, 60],
            Enumerable.Range(0, 6).Select(MpvOptionsBuilder.StreamBufferAfterStalls));
}

public class PlaybackHealthTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private static HealthSample S(int second, long drops, long decoderDrops = 0, bool playing = true,
        bool buffering = false, double? speed = null, long? size = null, double duration = 0) =>
        new(T0.AddSeconds(second), playing, buffering, drops, decoderDrops, 0, speed, 3, size, duration);

    [Fact]
    public void Steady_drops_while_playing_mean_the_gpu_is_struggling()
    {
        var h = new PlaybackHealth();
        for (var i = 0; i <= 6; i++) h.Add(S(i, drops: i * 3)); // 3 frames/s
        Assert.Equal(3, h.RenderDropsPerSecond, 3);
        Assert.True(h.RenderingStruggling);
        Assert.Contains(Finding.RenderingTooSlow, h.Findings("d3d11va", "hevc", 2160));
    }

    [Fact]
    public void Needs_enough_playing_time_before_judging()
    {
        var h = new PlaybackHealth();
        for (var i = 0; i <= 3; i++) h.Add(S(i, drops: i * 10));
        Assert.False(h.RenderingStruggling, "three seconds is too short to judge");
    }

    [Fact]
    public void Drops_while_paused_or_seeking_dont_count()
    {
        var h = new PlaybackHealth();
        for (var i = 0; i <= 10; i++) h.Add(S(i, drops: i * 5, playing: false));
        Assert.Equal(0, h.RenderDropsPerSecond);
        Assert.False(h.RenderingStruggling);
    }

    [Fact]
    public void Occasional_drops_are_fine()
    {
        var h = new PlaybackHealth();
        for (var i = 0; i <= 8; i++) h.Add(S(i, drops: i / 4)); // a drop every few seconds
        Assert.False(h.RenderingStruggling);
        Assert.Empty(h.Findings("d3d11va", "hevc", 2160));
    }

    [Fact]
    public void Old_samples_fall_out_of_the_window()
    {
        var h = new PlaybackHealth();
        for (var i = 0; i <= 6; i++) h.Add(S(i, drops: i * 3));
        for (var i = 7; i <= 30; i++) h.Add(S(i, drops: 18));
        Assert.Equal(0, h.RenderDropsPerSecond);
    }

    [Fact]
    public void Stalls_count_each_time_buffering_starts()
    {
        var h = new PlaybackHealth();
        bool[] pattern = [false, true, true, false, true, false, false, true];
        for (var i = 0; i < pattern.Length; i++) h.Add(S(i, 0, buffering: pattern[i]));
        Assert.Equal(3, h.Stalls);
        h.Reset();
        Assert.Equal(0, h.Stalls);
    }

    [Fact]
    public void A_download_slower_than_the_bitrate_is_blamed_on_the_network()
    {
        var h = new PlaybackHealth();
        // 7.2 GB over 2 hours = 1 MB/s needed; only 0.6 MB/s arriving.
        h.Add(S(0, 0, speed: 600_000, size: 7_200_000_000, duration: 7200));
        h.Add(S(1, 0, buffering: true, speed: 600_000, size: 7_200_000_000, duration: 7200));
        var f = h.Findings("d3d11va", "hevc", 2160);
        Assert.Contains(Finding.NetworkTooSlow, f);
        Assert.Contains("0.6 MB/s", h.Explain(Finding.NetworkTooSlow));
        Assert.Contains("1.0 MB/s", h.Explain(Finding.NetworkTooSlow));
    }

    [Fact]
    public void Software_decoding_of_4k_hevc_is_flagged()
    {
        var h = new PlaybackHealth();
        Assert.Contains(Finding.NoHardwareDecoding, h.Findings("no", "hevc", 2160));
        Assert.Contains(Finding.NoHardwareDecoding, h.Findings(null, "hevc", 2160));
        Assert.DoesNotContain(Finding.NoHardwareDecoding, h.Findings("no", "h264", 1080));
        Assert.DoesNotContain(Finding.NoHardwareDecoding, h.Findings("d3d11va", "hevc", 2160));
    }

    [Fact]
    public void Decoder_drops_are_reported_separately()
    {
        var h = new PlaybackHealth();
        for (var i = 0; i <= 6; i++) h.Add(S(i, drops: 0, decoderDrops: i * 2));
        var f = h.Findings("no", "hevc", 2160);
        Assert.Contains(Finding.DecoderTooSlow, f);
        Assert.DoesNotContain(Finding.RenderingTooSlow, f);
    }
}

public class RenderingIntegrationTests
{
    [MpvFact]
    public void Auto_switches_to_balanced_for_a_4k_video_and_back_for_smaller_ones()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("uhd_hevc.mkv"));
        PlayerHarness.WaitUntil(() => h.Player.RenderProfile == RenderProfile.Balanced, "balanced for 4K");
        Assert.Equal("bilinear", h.Player.Mpv.GetString("cscale"));
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"));
        PlayerHarness.WaitUntil(() => h.Player.RenderProfile == RenderProfile.High, "high below 4K");
        Assert.Equal("ewa_lanczossharp", h.Player.Mpv.GetString("cscale"));
    }

    [MpvFact]
    public void A_fixed_setting_is_respected_and_fallback_sticks()
    {
        using var h = new PlayerHarness(new PlayerSettings { RenderQuality = RenderQuality.High });
        h.OpenAndWait(TestEnvironment.Media("uhd_hevc.mkv"));
        Thread.Sleep(200);
        Assert.Equal(RenderProfile.High, h.Player.RenderProfile);

        h.Player.Settings.RenderQuality = RenderQuality.Auto;
        h.Player.FallBackToFastRendering();
        h.Player.ApplyRenderQuality();
        Assert.Equal(RenderProfile.Fast, h.Player.RenderProfile);
        Assert.Equal("audio", h.Player.Mpv.GetString("video-sync"));
    }

    [MpvFact]
    public void Health_samples_read_real_counters()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"));
        var s = h.Player.SampleHealth();
        Assert.NotNull(s.FrameDrops);
        Assert.NotNull(s.DecoderDrops);
        Assert.True(s.FileSize > 0);
        Assert.True(s.Duration > 0);
    }

    [MpvFact]
    public void Each_stall_makes_the_stream_wait_for_more_buffer()
    {
        using var h = new PlayerHarness();
        h.Player.Open(TestEnvironment.Media("sdr_h264.mp4"), streaming: true);
        PlayerHarness.WaitUntil(() => h.Player.State.VideoFormat is not null, "stream opened");
        h.Player.GrowStreamBuffer();
        Assert.Equal(10, h.Player.Mpv.GetDouble("cache-pause-wait"));
        h.Player.GrowStreamBuffer();
        Assert.Equal(20, h.Player.StreamBufferSeconds);
        Assert.Equal(20, h.Player.Mpv.GetDouble("cache-pause-wait"));
    }
}
