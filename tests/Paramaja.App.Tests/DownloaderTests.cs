using Paramaja.App.ViewModels;
using Paramaja.Core.Playback;

namespace Paramaja.App.Tests;

public class DownloaderTests
{
    /// <summary>
    /// A stand-in yt-dlp: <c>-J</c> prints a video offered in 2160p, 1080p and 720p; otherwise it
    /// "downloads" by writing a file named after the -f format it was given.
    /// </summary>
    private static YtDlp FakeYtDlp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "paramaja-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "yt-dlp");
        File.WriteAllText(script, """
            #!/bin/sh
            for a in "$@"; do [ "$a" = "-J" ] && { echo '{"title":"Test clip","formats":[{"vcodec":"none"},{"vcodec":"vp9","height":720},{"vcodec":"av01","height":2160},{"vcodec":"avc1","height":1080}]}'; exit 0; }; done
            while [ $# -gt 0 ]; do
              case "$1" in
                -f) fmt="$2"; shift ;;
                --print-to-file) print="$3"; shift 2 ;;
                -P) out="$2"; shift ;;
                -o) name="$2"; shift ;;
                -s) dry=1 ;;
              esac
              shift
            done
            [ -n "$dry" ] && { echo 1080; echo "$out/clip.mkv"; exit 0; }
            echo "PARAMAJA 100|100|10|0|avc1"
            echo "$fmt" > "$out/$name"
            echo "$out/$name" >> "$print"
            """.Replace("\r", ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new YtDlp(dir);
    }

    [Fact]
    public void Quality_labels()
    {
        Assert.Equal("Best available", QualityOption.For(0).Label);
        Assert.Equal("2160p (4K)", QualityOption.For(2160).Label);
        Assert.Equal("1080p (Full HD)", QualityOption.For(1080).Label);
        Assert.Equal("480p", QualityOption.For(480).Label);
    }

    [Fact]
    public async Task Download_button_opens_the_downloader_with_the_playing_link()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("https://youtube.com/watch?v=abc");
        await r.Vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(["https://youtube.com/watch?v=abc"], r.Dialogs.DownloaderOpenedWith);
    }

    [Fact]
    public void Only_web_links_can_be_downloaded()
    {
        using var r = new Rig();
        Assert.True(new DownloadDialogViewModel(r.Vm, "https://youtube.com/watch?v=abc").IsValid);
        Assert.Equal("", new DownloadDialogViewModel(r.Vm, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567").Url);
        Assert.False(new DownloadDialogViewModel(r.Vm, "not a link").IsValid);
    }

    [Fact]
    public async Task Picks_a_quality_and_downloads_it_with_the_best_audio()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in is a shell script
        using var r = new Rig(ytdlp: FakeYtDlp());
        var dialog = new DownloadDialogViewModel(r.Vm, "https://youtube.com/watch?v=abc");

        await dialog.CheckCommand.ExecuteAsync(null);
        Assert.Equal("Test clip", dialog.Title);
        Assert.Equal(["Best available", "2160p (4K)", "1080p (Full HD)", "720p (HD)"], dialog.Qualities.Select(q => q.Label));

        dialog.SelectedQuality = dialog.Qualities.Single(q => q.Height == 1080);
        await r.Vm.StartDownloadAsync(dialog.Url, dialog.SelectedQuality.Height);
        var file = Assert.IsType<string>(r.Vm.LastDownloadedFile);
        Assert.StartsWith(r.Paths.Downloads, file);
        Assert.Equal(YtDlp.Format(1080), File.ReadAllText(file).Trim());
    }
}
