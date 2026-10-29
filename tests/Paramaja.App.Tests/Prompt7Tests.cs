using Paramaja.App.ViewModels;
using Paramaja.App.Views;
using Paramaja.Core.Playback;
using Paramaja.Core.Torrent;

namespace Paramaja.App.Tests;

public class PlaylistTests
{
    private const string Magnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Album";

    private static TorrentInfo Album => new(1, "abc", "Album", [
        new TorrentFile(0, "Album/cover.jpg", 900_000, false),
        new TorrentFile(1, "Album/02 Second.flac", 40_000_000, false),
        new TorrentFile(2, "Album/01 First.flac", 30_000_000, false),
        new TorrentFile(3, "Album/03 Third.dsf", 90_000_000, false),
        new TorrentFile(4, "Album/album.cue", 1000, false),
    ], 3, "http://127.0.0.1:9/stream/1/3"); // the engine picks the largest file

    [Fact]
    public async Task Music_counts_and_an_album_starts_at_its_first_track()
    {
        using var r = new Rig();
        r.Torrents.OnAdd = _ => Album;
        await r.Vm.OpenInputAsync(Magnet);
        Assert.Equal(["01 First.flac", "02 Second.flac", "03 Third.dsf"], r.Vm.TorrentFiles.Select(f => f.Name));
        Assert.Equal(2, r.Vm.SelectedTorrentFile!.Index);
        Assert.Contains("Select 1 2", r.Torrents.Calls);
        Assert.Contains("Open http://127.0.0.1:9/stream/1/2 01 First.flac [stream]", r.Player.Calls);
        Assert.True(r.Vm.HasTorrentFiles);
    }

    [Fact]
    public async Task Previous_next_and_the_end_of_a_file_move_through_the_playlist()
    {
        using var r = new Rig();
        r.Torrents.OnAdd = _ => Album;
        await r.Vm.OpenInputAsync(Magnet);
        r.Vm.NextFileCommand.Execute(null);
        Assert.Equal("02 Second.flac", r.Vm.SelectedTorrentFile!.Name);
        Assert.Contains("Open http://127.0.0.1:9/stream/1/1 02 Second.flac [stream]", r.Player.Calls);

        // At its end mpv keeps it open (keep-open); the next one plays.
        r.Player.State.EofReached = true;
        r.Player.RaiseState("eof-reached");
        Assert.Equal("03 Third.dsf", r.Vm.SelectedTorrentFile!.Name);
        r.Player.RaiseState("eof-reached"); // the last: it stays
        Assert.Equal("03 Third.dsf", r.Vm.SelectedTorrentFile!.Name);

        r.Vm.PreviousFileCommand.Execute(null);
        r.Vm.PreviousFileCommand.Execute(null);
        r.Vm.PreviousFileCommand.Execute(null); // the first: it stays
        Assert.Equal("01 First.flac", r.Vm.SelectedTorrentFile!.Name);
    }

    [Fact]
    public async Task A_video_torrent_still_starts_at_its_main_video()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(Magnet);
        Assert.Equal(1, r.Vm.SelectedTorrentFile!.Index); // Sintel.mp4, the engine's pick
        Assert.DoesNotContain(r.Torrents.Calls, c => c.StartsWith("Select"));
    }
}

public class Prompt7ViewModelTests
{
    [Fact]
    public async Task Stop_stops_and_closes_a_torrent()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10");
        r.Vm.StopCommand.Execute(null);
        Assert.Contains("Stop", r.Player.Calls);
        Assert.False(r.Vm.IsTorrent);
    }

    [Fact]
    public void Audio_alone_has_no_frames_or_picture_options()
    {
        using var r = new Rig();
        r.Player.State.IsIdle = false;
        r.Player.RaiseState("idle-active");
        Assert.False(r.Vm.IsAudioOnly); // not known yet
        r.Player.State.HasVideo = false;
        r.Player.State.DisplayAspect = 1.0; // cover art
        r.Player.RaiseState("has-video");
        Assert.True(r.Vm.IsAudioOnly);
        Assert.False(r.Vm.HasPictureOptions);
        r.Player.State.HasVideo = true;
        r.Player.RaiseState("has-video");
        Assert.False(r.Vm.IsAudioOnly);
        Assert.True(r.Vm.HasPictureOptions);
    }

    [Fact]
    public async Task A_dvd_shows_as_dvd_on_either_engine_and_files_by_their_codec()
    {
        using var r = new Rig(); // no disc engine: mpv plays it
        var disc = Path.Combine(r.Root, "CONCERT");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS")).FullName, "VIDEO_TS.IFO"), "");
        await r.Vm.OpenInputAsync(disc);
        r.Player.State.VideoFormat = "mpeg2video";
        r.Player.RaiseState("video-format");
        Assert.Equal("DVD", r.Vm.CodecBadge);
        await r.Vm.OpenInputAsync(r.MediaFile());
        r.Player.RaiseState("video-format");
        Assert.Equal("MPEG-2", r.Vm.CodecBadge);
    }

    [Fact]
    public async Task An_sacd_image_plays_through_mpv()
    {
        using var r = new Rig(discEngine: true);
        var iso = Path.Combine(r.Root, "Thriller.iso");
        var data = new byte[600 * 2048];
        "SACDMTOC"u8.CopyTo(data.AsSpan(510 * 2048));
        File.WriteAllBytes(iso, data);
        await r.Vm.OpenInputAsync(iso);
        Assert.Contains($"OpenSacd {iso}", r.Player.Calls);
        Assert.False(r.Vm.IsDiscMode);
    }

    [Fact]
    public void Messages_start_below_the_top_bar_and_the_buffering_line_shows_once()
    {
        using var r = new Rig();
        r.Vm.SetOsdInset(OverlayPlacement.OsdInset(TopBar.BarHeight, 1.0, 720));
        Assert.Contains("SetOsdTopInset 0.1", r.Player.Calls);
        r.Player.Calls.Clear();
        r.Vm.SetOsdInset(0.1); // unchanged
        Assert.Empty(r.Player.Calls);

        r.Player.State.IsIdle = false;
        r.Player.RaiseState("idle-active");
        r.Player.State.IsBuffering = true;
        r.Player.RaiseState("paused-for-cache");
        r.Vm.BarsShown = true;
        r.Vm.TickAutoHide(DateTime.UtcNow);
        Assert.NotEmpty(r.Vm.BufferingText);
        Assert.DoesNotContain(r.Player.Calls, c => c.StartsWith("ShowText")); // the top bar shows it
        r.Vm.BarsShown = false;
        r.Vm.TickAutoHide(DateTime.UtcNow.AddSeconds(5));
        Assert.Contains(r.Player.Calls, c => c.StartsWith("ShowText"));
    }

    [Fact]
    public void The_osd_inset_is_the_bars_share_of_the_player()
    {
        Assert.Equal(0.1, OverlayPlacement.OsdInset(72, 1.0, 720), 6);
        Assert.Equal(0.075, OverlayPlacement.OsdInset(72, 1.5, 1440), 6);
        Assert.Equal(0, OverlayPlacement.OsdInset(72, 1.0, 0));
        Assert.Equal(0.5, OverlayPlacement.OsdInset(72, 1.0, 50)); // a tiny window
    }

    [Fact]
    public void Clicks_the_os_reports_replace_the_engines()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Vm.ClicksFromSystem = true;
        var fullscreen = 0;
        r.Vm.FullscreenRequested += _ => fullscreen++;
        r.Player.RaiseClick();
        r.Player.RaiseClick();
        Assert.Equal(0, fullscreen);
        var now = DateTime.UtcNow;
        r.Vm.HandleClick(now);
        r.Vm.HandleClick(now.AddMilliseconds(200));
        Assert.Equal(1, fullscreen);
    }

    [Fact]
    public async Task The_video_menu_changes_a_web_videos_quality_for_that_video()
    {
        using var r = new Rig();
        r.Settings.StreamMaxHeight = 1080;
        await r.Vm.OpenInputAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Assert.False(r.Vm.HasStreamQualities); // not known until the engine has the page
        r.Player.State.StreamHeights = [360, 2160, 1080, 720, 1080];
        r.Player.RaiseState("stream-heights");
        Assert.True(r.Vm.HasStreamQualities);
        Assert.Equal(["Best available", "2160p (4K)", "1080p", "720p", "360p"], r.Vm.StreamQualities.Select(q => q.Label));
        Assert.Equal(2, r.Vm.StreamQualityChoiceIndex); // the setting: up to 1080p
        r.Vm.StreamQualityChoiceIndex = 3;
        Assert.Contains("ReloadStream 720", r.Player.Calls);
        Assert.Equal(1080, r.Settings.StreamMaxHeight); // the default stays
        await r.Vm.OpenInputAsync(r.MediaFile());
        Assert.Empty(r.Vm.StreamQualities);
    }

    [Theory]
    [InlineData("https://archive.org/details/MichaelJacksonThriller")]
    [InlineData("https://vimeo.com/76979871")]
    public async Task Other_sites_get_no_quality_menu(string url)
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(url);
        r.Player.State.StreamHeights = [1080, 720, 480]; // even if yt-dlp found some
        r.Player.RaiseState("stream-heights");
        Assert.False(r.Vm.HasStreamQualities);
        Assert.Empty(r.Vm.StreamQualities);
    }

    [Fact]
    public async Task A_direct_link_gets_no_quality_menu_and_nothing_else_reads_it()
    {
        // MEGA's desktop app streams at http://127.0.0.1:4443/…: its server stalled when a second
        // client (yt-dlp, asked for qualities) read the file while mpv played it.
        using var r = new Rig();
        await r.Vm.OpenInputAsync("http://127.0.0.1:4443/7dhGXQwR/MJ%20in%20Cork%201988_02.mp4");
        Assert.Contains("Open http://127.0.0.1:4443/7dhGXQwR/MJ%20in%20Cork%201988_02.mp4 [stream]", r.Player.Calls);
        r.Player.RaiseState("stream-heights"); // none: yt-dlp never ran for it
        Assert.False(r.Vm.HasStreamQualities);
    }

    [Fact]
    public void A_quality_asked_for_maps_to_what_the_video_has()
    {
        var items = StreamQualityItem.For([1080, 720, 480]).ToList();
        Assert.Equal(0, StreamQualityItem.IndexFor(items, 0));
        Assert.Equal(1, StreamQualityItem.IndexFor(items, 2160)); // up to 4K: 1080p is the most there is
        Assert.Equal(2, StreamQualityItem.IndexFor(items, 720));
        Assert.Equal(3, StreamQualityItem.IndexFor(items, 360)); // below everything: the lowest
        Assert.Equal(-1, StreamQualityItem.IndexFor([], 720));
    }
}
