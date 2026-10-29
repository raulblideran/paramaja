using System.Buffers.Binary;
using LibVLCSharp.Shared;
using Paramaja.Core.Playback;
using Paramaja.Core.Playback.Sacd;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Tests;

public class PictureAndOsdTests
{
    [Theory]
    [InlineData(720, 576, 4.0 / 3, "720x432+0+72")]       // a 4:3 DVD: the top and bottom go
    [InlineData(720, 576, 1.25, "720x404+0+86")]          // square pixels
    [InlineData(1920, 800, 2.4, "1422x800+249+0")]        // scope: the sides go
    [InlineData(720, 480, 16.0 / 9, null)]                // anamorphic 16:9 already
    [InlineData(1920, 1080, 16.0 / 9, null)]
    [InlineData(null, 576, 4.0 / 3, null)]
    public void Crop_to_16_9_keeps_the_middle(int? w, int? h, double dar, string? crop) =>
        Assert.Equal(crop, MpvOptionsBuilder.CropTo(w, h, dar, 16.0 / 9));

    [Fact]
    public void Messages_clear_the_top_bar()
    {
        Assert.Equal(16 + 72, MpvOptionsBuilder.OsdMarginBelow(0.1, 16));
        Assert.Equal(16, MpvOptionsBuilder.OsdMarginBelow(0, 16));
        Assert.Equal(16 + 720, MpvOptionsBuilder.OsdMarginBelow(3, 16));
    }

    [Fact]
    public void Folders_play_dsd_files_too()
    {
        Assert.Contains(MpvOptionsBuilder.Build(new PlayerSettings(), new PortablePaths(TestEnvironment.TempDir())),
            o => o.Key == "audio-exts" && o.Value.Split(',').Contains("dsf") && o.Value.Split(',').Contains("dff"));
        Assert.Contains("audio-exts", MpvOptionsBuilder.OptionalOptions);
    }

    [Theory]
    [InlineData(StereoChannel.Both, 6, VlcDiscPlayer.OriginalChannels)] // 5.1 as 5.1: not libVLC's "Stereo"
    [InlineData(StereoChannel.Both, null, VlcDiscPlayer.OriginalChannels)]
    [InlineData(StereoChannel.Both, 2, AudioOutputChannel.Stereo)]
    [InlineData(StereoChannel.Left, 2, AudioOutputChannel.Left)]
    [InlineData(StereoChannel.Right, 2, AudioOutputChannel.Right)]
    public void Discs_keep_their_channels(StereoChannel channel, int? source, AudioOutputChannel mode) =>
        Assert.Equal(mode, VlcDiscPlayer.StereoMode(channel, source));
}

public class Prompt7PlaybackTests
{
    [MpvFact]
    public void Music_has_no_video_even_with_cover_art()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("music_cover.flac"));
        PlayerHarness.WaitUntil(() => h.Player.State.HasVideo is not null, "tracks");
        Assert.False(h.Player.State.HasVideo);
        Assert.Contains(h.Player.Tracks, t => t.Type == Playback.TrackType.Video && t.AlbumArt);
        h.OpenAndWait(TestEnvironment.Media("music.flac"));
        PlayerHarness.WaitUntil(() => h.Player.State.HasVideo is not null, "tracks");
        Assert.False(h.Player.State.HasVideo);
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"));
        PlayerHarness.WaitUntil(() => h.Player.State.HasVideo == true, "a video");
    }

    [MpvFact]
    public void The_picture_is_resized_or_cropped_for_that_video_only()
    {
        using var h = new PlayerHarness();
        h.OpenAndWait(TestEnvironment.Media("progressive_576.mp4"), paused: true);
        PlayerHarness.WaitUntil(() => h.Player.State.DisplayAspect is not null, "the shape");
        Assert.Equal(1.25, h.Player.State.DisplayAspect!.Value, 3);

        h.Player.SetPictureFit(PictureFit.Resize4x3);
        PlayerHarness.WaitUntil(() => Math.Abs((h.Player.Mpv.GetDouble("video-params/aspect") ?? 0) - 4.0 / 3) < 0.01, "4:3");
        Assert.Equal(1.25, h.Player.State.DisplayAspect!.Value, 3); // the source's shape, still

        h.Player.SetPictureFit(PictureFit.Crop16x9);
        if (h.Player.Mpv.GetString("video-crop") is { } crop) // mpv 0.38+
            Assert.Equal("720x404+0+86", crop);
        PlayerHarness.WaitUntil(() => Math.Abs((h.Player.Mpv.GetDouble("video-params/aspect") ?? 0) - 1.25) < 0.01, "the aspect override gone");

        h.OpenAndWait(TestEnvironment.Media("progressive_576.mp4"), paused: true);
        Assert.Equal(PictureFit.Original, h.Player.State.PictureFit);
        Assert.Equal("", h.Player.Mpv.GetString("video-crop") ?? "");
    }

    [MpvFact]
    public void Messages_start_below_the_bar()
    {
        using var h = new PlayerHarness();
        var margin = h.Player.Mpv.GetLong("osd-margin-y")!.Value;
        h.Player.SetOsdTopInset(0.1);
        Assert.Equal(margin + 72, h.Player.Mpv.GetLong("osd-margin-y"));
        h.Player.SetOsdTopInset(0);
        Assert.Equal(margin, h.Player.Mpv.GetLong("osd-margin-y"));
    }

    [MpvFact]
    public void Another_quality_reloads_the_video_where_it_was_for_it_only()
    {
        using var h = new PlayerHarness(new PlayerSettings { StreamMaxHeight = 1080 });
        h.OpenAndWait(TestEnvironment.Media("chapters.mkv"), paused: true);
        h.Player.SeekAbsolute(3);
        PlayerHarness.WaitUntil(() => Math.Abs(h.Player.State.Position - 3) < 0.2, "the seek");
        h.Player.ReloadStream(720);
        PlayerHarness.WaitUntil(() => (h.Player.Mpv.GetString("ytdl-format") ?? "").Contains("720"), "the format");
        h.WaitRestart();
        PlayerHarness.WaitUntil(() => Math.Abs(h.Player.State.Position - 3) < 0.3, "the same place");
        Assert.True(h.Player.State.IsPaused);
        h.OpenAndWait(TestEnvironment.Media("sdr_h264.mp4"));
        Assert.Contains("1080", h.Player.Mpv.GetString("ytdl-format"));
    }

    [MpvFact]
    public void Dsd_files_play()
    {
        var dir = TestEnvironment.TempDir();
        var dff = Path.Combine(dir, "tone.dff");
        var area = new SacdArea(SacdAreaKind.Stereo, 2, false, SacdImage.DsdRate, []);
        var frames = 150; // 2 s
        using (var file = File.Create(dff))
        {
            file.Write(SacdTrackStream.Header(area, frames, 2 * SacdImage.DsdBytesPerChannel, 0));
            file.Write(DsdTone(frames * 2 * SacdImage.DsdBytesPerChannel));
        }
        var dsf = Path.Combine(dir, "tone.dsf");
        WriteDsf(dsf, 2.0);

        using var h = new PlayerHarness();
        foreach (var path in new[] { dff, dsf })
        {
            h.OpenAndWait(path);
            PlayerHarness.WaitUntil(() => h.Player.State.SourceChannels == 2, $"{Path.GetExtension(path)} audio");
            PlayerHarness.WaitUntil(() => Math.Abs(h.Player.State.Duration - 2) < 0.05, $"{Path.GetExtension(path)} duration");
            Assert.False(h.Player.State.HasVideo);
            Assert.StartsWith("dsd", h.Player.Mpv.GetString("audio-codec-name"));
        }
    }

    [MpvFact]
    public void An_sacd_image_plays_its_tracks_as_chapters_and_its_areas_as_titles()
    {
        var path = SacdTestImage.Write(Path.Combine(TestEnvironment.TempDir(), "sacd.iso"), "Thriller", "Michael Jackson",
            new SacdTestImage.Area(true, 2, [new("One", 75), new("Two", 75), new("Three", 75)]),
            new SacdTestImage.Area(false, 6, [new("Surround One", 75), new("Surround Two", 75)]));
        using var h = new PlayerHarness();
        h.Player.OpenSacd(path);
        PlayerHarness.WaitUntil(() => h.Player.Chapters.Count == 3, "the tracks as chapters", 30);
        Assert.Equal(["One", "Two", "Three"], h.Player.Chapters.Select(c => c.Title));
        Assert.Equal(3, h.Player.State.Duration, 1);
        Assert.Equal("Michael Jackson — Thriller", h.Player.State.Title);
        PlayerHarness.WaitUntil(() => h.Player.State.SourceChannels == 2, "stereo audio");
        Assert.Equal(["Stereo (00:03)", "Multichannel 5.1 (00:02)"], h.Player.Titles.Select(t => t.Title));
        Assert.True(h.Player.Titles[0].Selected);

        h.Player.GoToChapter(2);
        PlayerHarness.WaitUntil(() => h.Player.State.Position >= 2, "the third track");

        h.Player.SelectTitle((int)SacdAreaKind.Multichannel);
        PlayerHarness.WaitUntil(() => h.Player.State.SourceChannels == 6, "5.1 audio", 30);
        PlayerHarness.WaitUntil(() => h.Player.Chapters.Count == 2, "its tracks");
        Assert.True(h.Player.Titles[1].Selected);
    }

    /// <summary>A 1 kHz tone as DSD: a first-order sigma-delta modulator, most significant bit first.</summary>
    private static byte[] DsdTone(int bytes, bool lsbFirst = false, int channels = 2)
    {
        var data = new byte[bytes];
        var integrators = new double[channels];
        for (var i = 0; i < bytes; i++)
        {
            var ch = i % channels;
            var sample = i / channels * 8;
            byte b = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                var x = 0.5 * Math.Sin(2 * Math.PI * 1000 * (sample + bit) / SacdImage.DsdRate);
                var one = integrators[ch] >= 0;
                integrators[ch] += x - (one ? 1 : -1);
                if (one) b |= (byte)(lsbFirst ? 1 << bit : 0x80 >> bit);
            }
            data[i] = b;
        }
        return data;
    }

    /// <summary>A DSF file: blocks of 4096 bytes per channel, least significant bit first.</summary>
    private static void WriteDsf(string path, double seconds)
    {
        const int block = 4096, channels = 2;
        var samples = (long)(seconds * SacdImage.DsdRate);
        var blocks = (int)((samples / 8 + block - 1) / block);
        var mono = DsdTone(blocks * block, lsbFirst: true, channels: 1);
        var dataSize = 12L + blocks * block * channels;
        using var f = new BinaryWriter(File.Create(path));
        void U32(uint v) => f.Write(v);
        void U64(ulong v) => f.Write(v);
        f.Write("DSD "u8);
        U64(28);
        U64((ulong)(28 + 52 + dataSize));
        U64(0);
        f.Write("fmt "u8);
        U64(52);
        U32(1);        // version
        U32(0);        // DSD raw
        U32(2);        // stereo
        U32(channels);
        U32(SacdImage.DsdRate);
        U32(1);        // bits per sample
        U64((ulong)samples);
        U32(block);
        U32(0);
        f.Write("data"u8);
        U64((ulong)dataSize);
        for (var b = 0; b < blocks; b++)
            for (var c = 0; c < channels; c++)
                f.Write(mono, b * block, block);
    }
}
