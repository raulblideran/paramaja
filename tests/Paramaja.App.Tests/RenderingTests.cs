using Paramaja.App.Services;
using Paramaja.App.ViewModels;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.App.Tests;

public class RenderingTests
{
    /// <summary>Feeds the view model a second-by-second run of samples dropping <paramref name="dropsPerSecond"/>.</summary>
    private static void Play(Rig r, int seconds, int dropsPerSecond, DateTime? start = null)
    {
        var t0 = start ?? DateTime.UtcNow;
        long drops = 0;
        for (var i = 0; i <= seconds; i++)
        {
            var at = t0.AddSeconds(i);
            var d = drops;
            r.Player.NextSample = () => new HealthSample(at, true, false, d, 0, 0, null, 2, null, 0);
            r.Vm.RefreshHealth();
            drops += dropsPerSecond;
        }
    }

    [Fact]
    public void Auto_falls_back_to_fast_when_frames_keep_dropping()
    {
        using var r = new Rig(new PlayerSettings { RenderQuality = RenderQuality.Auto });
        r.Vm.IsIdle = false;
        Play(r, seconds: 8, dropsPerSecond: 4);
        Assert.Contains("FallBackToFastRendering", r.Player.Calls);
        Assert.Contains(r.Player.Calls, c => c.StartsWith("ShowText Frames were dropping"));
        Assert.Contains("Fast rendering", r.Vm.StatusMessage);
        Assert.Equal("Auto is using Fast", r.Vm.RenderProfileText);
    }

    [Fact]
    public void Smooth_playback_keeps_the_profile()
    {
        using var r = new Rig(new PlayerSettings { RenderQuality = RenderQuality.Auto });
        r.Vm.IsIdle = false;
        Play(r, seconds: 20, dropsPerSecond: 0);
        Assert.DoesNotContain("FallBackToFastRendering", r.Player.Calls);
    }

    [Fact]
    public void A_chosen_profile_is_never_overridden()
    {
        using var r = new Rig(new PlayerSettings { RenderQuality = RenderQuality.High });
        r.Vm.IsIdle = false;
        Play(r, seconds: 10, dropsPerSecond: 10);
        Assert.DoesNotContain("FallBackToFastRendering", r.Player.Calls);
    }

    [Fact]
    public void Falls_back_only_once()
    {
        using var r = new Rig(new PlayerSettings { RenderQuality = RenderQuality.Auto });
        r.Vm.IsIdle = false;
        Play(r, seconds: 8, dropsPerSecond: 4);
        Play(r, seconds: 8, dropsPerSecond: 4, DateTime.UtcNow.AddMinutes(1));
        Assert.Single(r.Player.Calls, "FallBackToFastRendering");
    }

    [Fact]
    public void Rendering_setting_applies_and_persists()
    {
        using var r = new Rig();
        Assert.Equal(2, r.Vm.RenderQualityIndex); // Balanced by default
        r.Vm.RenderQualityIndex = 3;
        Assert.Equal(RenderQuality.Fast, r.Settings.RenderQuality);
        Assert.Contains("ApplyRenderQuality Fast", r.Player.Calls);
        Assert.Equal(RenderQuality.Fast, r.Store.Load().RenderQuality);
    }

    [Fact]
    public async Task Diagnostics_are_copied_with_a_verdict_and_no_file_names()
    {
        using var r = new Rig(new PlayerSettings { RenderQuality = RenderQuality.Auto });
        r.Vm.IsIdle = false;
        r.Player.State.Title = "Secret.Movie.2160p.mkv";
        r.Player.State.VideoFormat = "hevc";
        r.Player.State.VideoWidth = 3840;
        r.Player.State.VideoHeight = 2160;
        r.Player.State.HwdecCurrent = "d3d11va";
        Play(r, seconds: 3, dropsPerSecond: 0);
        await r.Vm.OpenInputAsync("magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10");
        r.Vm.HandleKey("Ctrl+i");
        var report = r.Dialogs.Clipboard2!;
        Assert.Contains(AppInfo.Display, report);
        Assert.Contains("Source: Torrent", report);
        Assert.Contains("hevc 3840x2160", report);
        Assert.Contains("hardware (d3d11va)", report);
        Assert.Contains("Verdict:", report);
        Assert.DoesNotContain("Secret.Movie", report);
        Assert.Contains("Diagnostics copied", r.Vm.StatusMessage);
    }

    [Fact]
    public void Report_explains_a_struggling_gpu()
    {
        var health = new PlaybackHealth();
        var t0 = DateTime.UtcNow;
        for (var i = 0; i <= 8; i++) health.Add(new HealthSample(t0.AddSeconds(i), true, false, i * 5L, 0, 0, null, 2, null, 0));
        var state = new PlayerState { VideoFormat = "hevc", VideoWidth = 3840, VideoHeight = 2160, HwdecCurrent = "d3d11va" };
        var report = DiagnosticsReport.Build(state, new PlayerSettings { RenderQuality = RenderQuality.Auto }, RenderProfile.High, health, MediaSource.Local, "",
            os: "Windows 11", cpu: "Some CPU", gpus: ["Intel(R) UHD Graphics 620 (driver 31.0)"], memory: "8 GB");
        Assert.Contains("GPU: Intel(R) UHD Graphics 620", report);
        Assert.Contains("graphics card can't keep up", report);
        Assert.Contains("Rendering: High (setting: Auto)", report);
    }

    [Theory]
    [InlineData("0x10de", "NVIDIA")]
    [InlineData("0x1002", "AMD")]
    [InlineData("0x8086", "Intel")]
    [InlineData("0x1234", "0x1234")]
    public void Pci_vendor_names(string id, string name) => Assert.Equal(name, SystemInfo.VendorName(id));

    [Fact]
    public void System_info_never_throws()
    {
        Assert.False(string.IsNullOrEmpty(SystemInfo.Os));
        Assert.NotNull(SystemInfo.Cpu);
        Assert.NotNull(SystemInfo.Gpus);
        Assert.NotNull(SystemInfo.Memory);
    }
}
