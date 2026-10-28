using Paramaja.Core.Playback;

namespace Paramaja.Core.Tests;

/// <summary>What the disc engine reads from libVLC: its log lines and its track lists.</summary>
public class DiscPlayerParsingTests
{
    [Theory]
    [InlineData("Detected interlaced video", true)]
    [InlineData("Detected progressive video", false)]
    [InlineData("deinterlace -1, mode yadif2x, is_needed 1", true)]   // as libVLC 3 logs it (seen on a DVD)
    [InlineData("deinterlace -1, mode yadif2x, is_needed 0", false)]  // a new title's video output starts here
    [InlineData("deinterlace 0, mode yadif2x, is_needed 1", true)]    // switched off, still interlaced
    [InlineData("deinterlace 1, mode , is_needed 0", false)]
    [InlineData("using video filter module \"deinterlace\"", null)]
    [InlineData("Detected interlaced video in the wild", true)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Interlacing_is_read_from_libVLCs_log(string? message, bool? interlaced) =>
        Assert.Equal(interlaced, VlcDiscPlayer.InterlacedFromLog(message));

    [Fact]
    public void A_titles_audio_channels_are_matched_to_the_ids_libVLC_plays_them_by()
    {
        // libVLC's list after the menu (title 0) and the main title: each title adds its own tracks.
        (bool, bool, int)[] tracks =
        [
            (false, false, 0), (true, false, 0), (false, true, 2),               // menu: subtitles, video, stereo
            (true, false, 0), (false, true, 6), (false, true, 2), (false, true, 6), // main title: video, 5.1, stereo, DTS
        ];
        var map = VlcDiscPlayer.MatchChannels(tracks, [128, 160, 136]);
        Assert.Equal(new Dictionary<int, int> { [128] = 6, [160] = 2, [136] = 6 }, map);

        // An Audio CD's track: no video, one stereo track.
        Assert.Equal(new Dictionary<int, int> { [1] = 2 }, VlcDiscPlayer.MatchChannels([(false, true, 2)], [1]));
        // Lists that don't line up (libVLC still adding tracks): nothing is guessed.
        Assert.Empty(VlcDiscPlayer.MatchChannels(tracks, [128, 160]));
        // Unknown channel counts aren't reported.
        Assert.Empty(VlcDiscPlayer.MatchChannels([(true, false, 0), (false, true, 0)], [192]));
    }
}

public class GpuTimeTests
{
    [Fact]
    public void Gpu_time_per_frame_adds_up_mpvs_render_passes()
    {
        // As gpu-next reports it (abridged): times in nanoseconds.
        const string passes = """{"fresh":[{"desc":"color decoding","last":26360,"avg":28855,"peak":49120,"count":119,"samples":[34160,33600]},""" +
                              """{"desc":"scaling","last":900000,"avg":1000000,"peak":1500000,"count":119,"samples":[]}],"redraw":[]}""";
        Assert.Equal(1028.855, PlayerService.GpuFrameTime(passes)!.Value, 3);
        Assert.Null(PlayerService.GpuFrameTime("""{"fresh":[],"redraw":[]}"""));
        Assert.Null(PlayerService.GpuFrameTime(null));
        Assert.Null(PlayerService.GpuFrameTime("not json"));
    }
}
