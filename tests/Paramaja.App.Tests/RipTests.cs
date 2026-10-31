using System.Buffers.Binary;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Paramaja.App.Services;
using Paramaja.App.ViewModels;
using Paramaja.App.Views;
using Paramaja.Core.Ripping.Cd;
using Paramaja.Core.Ripping.Dvd;

namespace Paramaja.App.Tests;

public class RipDialogTests
{
    private static readonly OpticalDrive Cd = new("/dev/sr0", "sr0", "Audio CD");
    private static readonly OpticalDrive Dvd = new("/run/media/me/CONCERT", "sr1", "CONCERT");

    /// <summary>Three tracks of seeded noise standing in for a CD in a drive.</summary>
    private static ImageCdDrive CdDrive()
    {
        var audio = new byte[900 * CdToc.SectorBytes];
        new Random(3).NextBytes(audio);
        var toc = new CdToc([new CdTrack(1, 0, true), new CdTrack(2, 300, true), new CdTrack(3, 600, true)], 900);
        return new ImageCdDrive(new MemoryStream(audio), toc)
        {
            CdText = new CdText("Test Album", "Test Artist", new Dictionary<int, string> { [1] = "One", [2] = "Two", [3] = "Three" },
                new Dictionary<int, string>()),
        };
    }

    private static DvdInfo DvdDisc() => new()
    {
        Name = "Concert",
        Titles =
        [
            new DvdTitle(1, 1, 1, 1, TimeSpan.FromMinutes(2), [], []),
            new DvdTitle(2, 1, 12, 1, TimeSpan.FromMinutes(95), [new DvdAudio("AC-3", 6, "en")], ["en"]),
        ],
        Files = [("VIDEO_TS.IFO", 2048L)],
    };

    private static RipDialogViewModel Dialog(Rig r, OpticalDrive drive, ImageCdDrive? cd = null) =>
        new(r.Vm, [drive], drive.Root, _ => cd ?? CdDrive(), _ => DvdDisc(), flac: "", ffmpeg: "/usr/bin/ffmpeg", online: false,
            ffprobe: "/usr/bin/ffprobe"); // named, so no machine's own tools (or their absence, on Windows) count

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task An_audio_cd_reads_with_its_cd_text_and_rips_to_the_music_folder()
    {
        using var r = new Rig();
        var vm = Dialog(r, Cd);
        await Until(() => vm.IsCd);
        Assert.Equal("Test Artist", vm.AlbumArtist);
        Assert.Equal("Test Album", vm.Album);
        Assert.Equal(["One", "Two", "Three"], vm.Tracks.Select(t => t.Title));
        Assert.Equal(1, vm.FormatIndex); // no FLAC encoder given: WAV
        Assert.Contains("isn't in AccurateRip's list", vm.OffsetNote);
        Assert.Equal(r.Paths.Music, vm.CdFolder);

        vm.Tracks[1].Title = "Two (Remix)";
        vm.OffsetText = "+6";
        Assert.Equal(6, vm.Offset);
        Assert.True(vm.StartCommand.CanExecute(null));
        vm.StartCommand.Execute(null);
        await Until(() => !r.Vm.IsRipping && r.Vm.RipResult.Length > 0);
        Assert.StartsWith("Ripped 3 tracks", r.Vm.RipResult);
        var folder = Path.Combine(r.Paths.Music, "Test Artist", "Test Album");
        Assert.Equal(folder, r.Vm.LastRipFolder);
        Assert.True(File.Exists(Path.Combine(folder, "02 - Two (Remix).wav")));
        Assert.Contains("6 (set by hand)", File.ReadAllText(Path.Combine(folder, "Test Artist - Test Album.log")));
        vm.Dispose();
    }

    [Fact]
    public async Task A_dvd_offers_its_titles_with_the_longest_first_picked()
    {
        using var r = new Rig();
        var vm = Dialog(r, Dvd);
        Assert.True(vm.IsDvd);
        Assert.Equal(2, vm.SelectedTitle!.Number);
        Assert.True(vm.IsMkv);
        Assert.Equal("/dev/sr1", vm.Source); // the raw drive, for its keys
        Assert.Equal(r.Paths.Videos, vm.DvdFolder);
        vm.DvdModeIndex = 1;
        Assert.False(vm.IsMkv);
        vm.UseDvdSource("/home/me/Concert.iso");
        Assert.Equal("/home/me/Concert.iso", vm.Source);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_drive_without_a_disc_or_with_something_else_says_so()
    {
        using var r = new Rig();
        var vm = new RipDialogViewModel(r.Vm, [new OpticalDrive("/dev/sr0", "sr0", null)], online: false);
        Assert.Equal(RipDiscKind.None, vm.Kind);
        Assert.False(vm.StartCommand.CanExecute(null));
        var data = new RipDialogViewModel(r.Vm, [Dvd], Dvd.Root, readDvd: _ => throw new InvalidDataException("no VIDEO_TS folder on this disc"),
            online: false);
        Assert.Contains("isn't an Audio CD or a DVD-Video", data.Message);
    }

    [Fact]
    public async Task Open_disc_hands_a_drive_to_the_ripper()
    {
        using var r = new Rig();
        r.Dialogs.NextDisc = MainViewModel.RipPrefix + "/dev/sr0";
        await r.Vm.OpenDiscCommand.ExecuteAsync(null);
        Assert.Equal(["/dev/sr0"], r.Dialogs.RipperOpenedWith);
        await r.Vm.OpenRipperCommand.ExecuteAsync(null);
        Assert.Equal(["/dev/sr0", null], r.Dialogs.RipperOpenedWith);
    }

    [Fact]
    public async Task A_cancelled_rip_says_nothing_was_kept()
    {
        using var r = new Rig();
        var audio = new byte[6000 * CdToc.SectorBytes];
        var toc = new CdToc([new CdTrack(1, 0, true), new CdTrack(2, 3000, true)], 6000);
        var drive = new ImageCdDrive(new MemoryStream(audio), toc);
        var disc = await r.Vm.CdRipper.ReadDiscAsync(drive, online: false);
        var rip = r.Vm.StartCdRipAsync(drive, disc, new CdRipOptions
        {
            Tags = disc.DefaultTags, Format = CdRipFormat.Wav, OutputRoot = r.Paths.Music, Offset = 0,
        });
        r.Vm.CancelRipCommand.Execute(null);
        await rip;
        Assert.False(r.Vm.IsRipping);
        Assert.Contains("cancelled", r.Vm.RipResult);
    }

    [Theory]
    [InlineData("Copy", 3, 14, 0.42, 8.1, "Track 3/14 · Copy · 42% · 8.1×")]
    [InlineData("Finding the gaps between tracks", 0, 14, 0, 0, "Finding the gaps between tracks…")]
    [InlineData("Writing title 1", 0, 0, 0.37, 0, "Writing title 1 · 37%")]
    public void Progress_reads_well(string stage, int track, int tracks, double fraction, double speed, string text) =>
        Assert.Equal(text, MainViewModel.DescribeRip(new Core.Ripping.RipProgress(stage, track, tracks, fraction, speed)));
}

public class RipUiTests
{
    [AvaloniaFact]
    public void The_rip_window_shows_a_cds_tags_and_tracks()
    {
        using var r = new Rig();
        var audio = new byte[300 * CdToc.SectorBytes];
        var toc = new CdToc([new CdTrack(1, 0, true), new CdTrack(2, 150, true)], 300);
        var drive = new ImageCdDrive(new MemoryStream(audio), toc);
        var vm = new RipDialogViewModel(r.Vm, [new OpticalDrive("/dev/sr0", "sr0", "Audio CD")], "/dev/sr0", _ => drive, flac: "/usr/bin/flac",
            online: false);
        for (var i = 0; i < 200 && !vm.IsCd; i++) { Thread.Sleep(10); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
        var w = new RipDialog { DataContext = vm };
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCd);
        Assert.Equal("Unknown Artist", w.FindControl<TextBox>("AlbumArtist")!.Text);
        Assert.Equal(0, w.FindControl<ComboBox>("Format")!.SelectedIndex); // FLAC, level 8
        Assert.True(w.FindControl<Button>("StartButton")!.IsEffectivelyEnabled);
        Assert.NotNull(w.CaptureRenderedFrame());
        w.Close();
    }

    [AvaloniaFact]
    public void The_top_bar_shows_a_rip_going_on()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new TopBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        Assert.False(bar.FindControl<TextBlock>("RipStatus")!.IsVisible);
        r.Vm.IsRipping = true;
        r.Vm.RipStatus = "Track 3/14 · Copy · 42%";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(bar.FindControl<TextBlock>("RipStatus")!.IsVisible);
        bar.Close();
    }
}
