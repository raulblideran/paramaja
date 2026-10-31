using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Tests;

public class Prompt8PlaybackTests
{
    [MpvFact]
    public void Hi_res_music_plays_at_its_own_rate()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("hires_96k.flac"));
        PlayerHarness.WaitUntil(() => h.Player.State.OutputSampleRate == 96_000, "96 kHz out");
        Assert.Equal(96_000, h.Player.State.SourceSampleRate);
    }

    [MpvFact]
    public void Surround_keeps_every_channel_unless_downmixed()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("audio_71.mkv"));
        PlayerHarness.WaitUntil(() => h.Player.State.OutputChannels == 8, "7.1 out");
        h.Player.SetDownmix(true);
        PlayerHarness.WaitUntil(() => h.Player.State.OutputChannels == 2, "the downmix");
        h.Player.SetDownmix(false);
        PlayerHarness.WaitUntil(() => h.Player.State.OutputChannels == 8, "7.1 again");
    }

    [MpvFact]
    public void A_file_without_chapters_after_one_with_them_has_none()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("chapters.mkv"), paused: true);
        PlayerHarness.WaitUntil(() => h.Player.Chapters.Count > 1, "chapters");
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"), paused: true);
        PlayerHarness.WaitUntil(() => h.Player.Chapters.Count == 0, "no chapters");
        Thread.Sleep(300);
        Assert.Empty(h.Player.Chapters);
    }
}
