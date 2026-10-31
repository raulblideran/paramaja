using System.Net;
using System.Net.Sockets;
using Paramaja.Core.Playback;
using Paramaja.Core.Ripping.Dvd;

namespace Paramaja.Core.Tests;

public class Prompt9RipTests
{
    private static readonly DvdRipOptions Title3 = new() { Source = "/dev/sr0", Title = 3, OutputRoot = "/out" };

    [Fact]
    public void A_copy_keeps_the_video_as_it_is()
    {
        Assert.Empty(DvdRipper.VideoArguments(DvdVideo.Copy, VideoScan.TopFieldFirst));
        var args = DvdRipper.MkvArguments(Title3, preindex: false, lpcm: [], "Concert", "/out/Concert.mkv", VideoScan.TopFieldFirst).ToList();
        Assert.DoesNotContain("-c:v", args);
    }

    [Fact]
    public void X264_keeps_interlaced_video_interlaced_with_the_discs_field_order()
    {
        Assert.Equal(["-c:v", "libx264", "-preset", "slow", "-crf", "16"], DvdRipper.VideoArguments(DvdVideo.X264, VideoScan.Progressive));
        Assert.Equal(["-c:v", "libx264", "-preset", "slow", "-crf", "16", "-flags:v", "+ilme+ildct", "-x264-params", "tff=1"],
            DvdRipper.VideoArguments(DvdVideo.X264, VideoScan.TopFieldFirst));
        Assert.Equal("bff=1", DvdRipper.VideoArguments(DvdVideo.X264, VideoScan.BottomFieldFirst).Last());
    }

    [Fact]
    public void X265_deinterlaces_interlaced_video_to_double_rate_and_leaves_progressive_alone()
    {
        Assert.Equal(["-c:v", "libx265", "-preset", "slow", "-crf", "18", "-x265-params", "no-sao=1:aq-mode=3:log-level=error"],
            DvdRipper.VideoArguments(DvdVideo.X265, VideoScan.Progressive));
        Assert.Equal(["-filter:v", "bwdif=mode=send_field:parity=auto:deint=all",
                "-c:v", "libx265", "-preset", "slow", "-crf", "16", "-x265-params", "no-sao=1:aq-mode=3:log-level=error"],
            DvdRipper.VideoArguments(DvdVideo.X265, VideoScan.TopFieldFirst));
    }

    [Fact]
    public void An_encoded_mkv_still_copies_the_audio_and_subtitles_and_turns_lpcm_into_flac()
    {
        var o = Title3 with { Video = DvdVideo.X265 };
        var args = DvdRipper.MkvArguments(o, preindex: false, lpcm: [0], "Concert", "/out/Concert.mkv", VideoScan.TopFieldFirst).ToList();
        Assert.Equal(["-map", "0", "-c", "copy", "-c:a:0", "flac", "-compression_level:a:0", "8",
            "-filter:v", "bwdif=mode=send_field:parity=auto:deint=all", "-c:v", "libx265", "-preset", "slow", "-crf", "16",
            "-x265-params", "no-sao=1:aq-mode=3:log-level=error", "-metadata", "title=Concert", "-f", "matroska", "/out/Concert.mkv"],
            args.SkipWhile(a => a != "-map"));
    }

    [Fact]
    public void The_field_order_comes_from_the_probe()
    {
        Assert.Equal("tt", DvdRipper.ParseFieldOrder("""
            {"streams": [{"codec_name": "mpeg2video", "codec_type": "video", "field_order": "tt"},
              {"codec_name": "ac3", "codec_type": "audio"}], "format": {"duration": "60.0"}}
            """));
        Assert.Null(DvdRipper.ParseFieldOrder("""{"streams": [{"codec_name": "mpeg2video", "codec_type": "video"}]}"""));
    }

    [Fact]
    public void The_picture_sample_is_read_from_every_idet_report()
    {
        // FFmpeg set the filter graph up twice: the first report is empty.
        const string log = """
            [Parsed_idet_0 @ 0x558b91ba1f80] Multi frame detection: TFF:     0 BFF:     0 Progressive:     0 Undetermined:     0
            [Parsed_idet_0 @ 0x7f19a4004a00] Single frame detection: TFF:   480 BFF:     0 Progressive:    15 Undetermined:     6
            [Parsed_idet_0 @ 0x7f19a4004a00] Multi frame detection: TFF:   501 BFF:     0 Progressive:     0 Undetermined:     0
            """;
        Assert.Equal(VideoScan.TopFieldFirst, DvdRipper.ParseIdet(log));
        Assert.Equal(VideoScan.Progressive,
            DvdRipper.ParseIdet("[Parsed_idet_0 @ 0x1] Multi frame detection: TFF:    12 BFF:     3 Progressive:   480 Undetermined:     5"));
        Assert.Equal(VideoScan.BottomFieldFirst,
            DvdRipper.ParseIdet("[Parsed_idet_0 @ 0x1] Multi frame detection: TFF:     2 BFF:   400 Progressive:    10 Undetermined:     5"));
        Assert.Null(DvdRipper.ParseIdet("[Parsed_idet_0 @ 0x1] Multi frame detection: TFF: 0 BFF: 0 Progressive: 0 Undetermined: 500"));
    }

    [Theory]
    [InlineData("tt", VideoScan.TopFieldFirst, VideoScan.TopFieldFirst)]
    [InlineData("tt", VideoScan.Progressive, VideoScan.Progressive)] // a film flagged interlaced
    [InlineData("tt", null, VideoScan.TopFieldFirst)] // a sample that couldn't tell (black): the flags
    [InlineData("bb", null, VideoScan.BottomFieldFirst)]
    [InlineData("progressive", VideoScan.TopFieldFirst, VideoScan.Progressive)]
    [InlineData(null, VideoScan.TopFieldFirst, VideoScan.Progressive)]
    public void Flags_say_and_the_picture_confirms(string? fieldOrder, VideoScan? sample, VideoScan expected) =>
        Assert.Equal(expected, DvdRipper.Decide(fieldOrder, sample));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(12, 4)]
    public void The_sample_is_a_third_of_the_way_in(int chapters, int expected) => Assert.Equal(expected, DvdRipper.SampleChapter(chapters));

    [Fact]
    public void The_encoders_come_from_ffmpegs_list()
    {
        const string list = """
            Encoders:
             V..... = Video
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D libx264rgb           libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 RGB (codec h264)
             V....D libx265              libx265 H.265 / HEVC (codec hevc)
             V.S..D mpeg2video           MPEG-2 video
             A....D flac                 FLAC (Free Lossless Audio Codec)
            """;
        Assert.Equal(["libx264", "libx265"], DvdRipper.ParseEncoders(list.Replace("\n", "\r\n")).Order());
        Assert.Empty(DvdRipper.ParseEncoders(" V....D mpeg2video           MPEG-2 video\n"));
    }
}

public class Prompt9StreamTests
{
    /// <summary>Serves one file over HTTP at a fixed rate, as a slow stream would arrive.</summary>
    private sealed class SlowServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public SlowServer(string file, int bytesPerSecond)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/{Path.GetFileName(file)}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            var data = File.ReadAllBytes(file);
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            context.Response.ContentType = "video/mp4";
                            context.Response.ContentLength64 = data.Length;
                            var chunk = Math.Max(1, bytesPerSecond / 20);
                            for (var at = 0; at < data.Length; at += chunk)
                            {
                                await context.Response.OutputStream.WriteAsync(data.AsMemory(at, Math.Min(chunk, data.Length - at)));
                                await Task.Delay(50);
                            }
                            context.Response.Close();
                        }
                        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
                    });
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    [MpvFact]
    public void A_link_starts_by_itself_once_its_first_five_seconds_are_in()
    {
        var file = TestEnvironment.Media("progressive_576.mp4"); // 6 s
        using var server = new SlowServer(file, (int)(new FileInfo(file).Length / 3)); // in about 3 s
        using var h = new PlayerHarness();
        h.Player.Open(server.Url, streaming: true);
        Assert.True(h.Player.State.IsStartingStream);
        var waits = new List<double>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        // Nothing but waiting: no Play, no unpause.
        while (h.Player.State.IsStartingStream && DateTime.UtcNow < deadline)
        {
            // The link's own options apply once its file starts (mpv's default before that is 1 s).
            if (h.Player.Mpv.GetString("path") == server.Url && h.Player.Mpv.GetDouble("cache-pause-wait") is { } w) waits.Add(w);
            Thread.Sleep(50);
        }
        Assert.False(h.Player.State.IsStartingStream, "the link never started");
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetDouble("time-pos") > 0.5, "playing");
        Assert.False(h.Player.QueryPaused());
        // The wait before it first played isn't a stall: it stays at 5 s, and so does the next one's.
        Assert.NotEmpty(waits);
        Assert.All(waits, w => Assert.Equal(5, w));
        Assert.Equal(5, h.Player.StreamBufferSeconds);
    }

    [MpvFact]
    public void A_file_opened_after_a_link_is_not_a_starting_stream()
    {
        using var h = new PlayerHarness();
        h.Player.Open(TestEnvironment.Media("sdr_h264.mp4"), streaming: true);
        Assert.True(h.Player.State.IsStartingStream);
        h.OpenAndWait(TestEnvironment.Media("progressive_576.mp4"));
        Assert.False(h.Player.State.IsStartingStream);
    }

    [MpvFact]
    public void Files_appended_play_after_the_first()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"), paused: true);
        h.Player.Append(TestEnvironment.Media("progressive_576.mp4"));
        PlayerHarness.WaitUntil(() => h.Player.Mpv.GetLong("playlist-count") == 2, "two files queued");
        Assert.Equal(0, h.Player.Mpv.GetLong("playlist-pos"));
    }
}
