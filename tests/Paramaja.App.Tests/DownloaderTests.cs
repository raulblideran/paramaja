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
            for a in "$@"; do [ "$a" = "-x" ] && audio=1; done
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
            [ -n "$dry" ] && [ -n "$audio" ] && { echo NA; echo "$out/clip.webm"; exit 0; }
            [ -n "$dry" ] && { echo 1080; echo "$out/clip.mkv"; exit 0; }
            [ -n "$audio" ] && name=$(echo "$name" | sed 's/%(ext)s/opus/')
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
        // Opened with a link: its qualities load by themselves (no button to press).
        var dialog = new DownloadDialogViewModel(r.Vm, "https://youtube.com/watch?v=abc");
        await dialog.Lookup;
        Assert.Equal("Test clip", dialog.Title);
        Assert.Equal(["Best available", "2160p (4K)", "1080p (Full HD)", "720p (HD)", "Audio only (best quality)"],
            dialog.Qualities.Select(q => q.Label));
        Assert.True(dialog.StartCommand.CanExecute(null));

        dialog.SelectedQuality = dialog.Qualities.Single(q => q.Height == 1080);
        await r.Vm.StartDownloadAsync(dialog.Url, dialog.SelectedQuality.Height);
        var file = Assert.IsType<string>(r.Vm.LastDownloadedFile);
        Assert.StartsWith(r.Paths.Downloads, file);
        Assert.Equal(YtDlp.Format(1080), File.ReadAllText(file).Trim());
    }

    [Fact]
    public async Task Audio_only_downloads_the_best_audio_alone()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in is a shell script
        using var r = new Rig(ytdlp: FakeYtDlp());
        var dialog = new DownloadDialogViewModel(r.Vm, "https://youtube.com/watch?v=abc");
        await dialog.Lookup;
        dialog.SelectedQuality = dialog.Qualities.Single(q => q.AudioOnly);
        Assert.False(dialog.WithBestAudio); // no "+ best audio" next to it
        await r.Vm.StartDownloadAsync(dialog.Url, dialog.SelectedQuality.Height, dialog.SelectedQuality.AudioOnly);
        var file = Assert.IsType<string>(r.Vm.LastDownloadedFile);
        Assert.Equal("clip.opus", Path.GetFileName(file));
        Assert.Equal(YtDlp.AudioFormat, File.ReadAllText(file).Trim());

        // The same audio again gets a number rather than overwriting.
        await r.Vm.StartDownloadAsync(dialog.Url, 0, audioOnly: true);
        Assert.Equal("clip (1).opus", Path.GetFileName(r.Vm.LastDownloadedFile));
    }

    [Fact]
    public async Task A_pasted_link_looks_up_its_qualities_and_a_newer_one_replaces_it()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in is a shell script
        using var r = new Rig(ytdlp: FakeYtDlp());
        var dialog = new DownloadDialogViewModel(r.Vm) { LookupDelay = TimeSpan.FromMilliseconds(200) };
        Assert.Empty(dialog.Qualities);

        dialog.Url = "https://youtube.com/watch?v=first";
        var first = dialog.Lookup;
        dialog.Url = "https://youtube.com/watch?v=second"; // typed on before the pause ended
        await first;                                      // cancelled: shows nothing
        Assert.Empty(dialog.Qualities);
        await dialog.Lookup;
        Assert.Equal("Test clip", dialog.Title);
        Assert.Equal(5, dialog.Qualities.Count);

        dialog.Url = "not a link";
        Assert.Empty(dialog.Qualities);
        Assert.False(dialog.IsChecking);
        Assert.False(dialog.StartCommand.CanExecute(null));
    }
}
