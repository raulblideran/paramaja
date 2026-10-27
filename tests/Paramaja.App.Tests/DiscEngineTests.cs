using Paramaja.App.Services;
using Paramaja.Core.Input;
using Paramaja.Core.Playback;

namespace Paramaja.App.Tests;

public class DiscEngineTests
{
    private static string DvdFolder()
    {
        var disc = Path.Combine(Path.GetTempPath(), "paramaja-app-tests", Guid.NewGuid().ToString("N"), "MOVIE");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS")).FullName, "VIDEO_TS.IFO"), "");
        return disc;
    }

    [Fact]
    public async Task Discs_play_through_the_disc_engine_and_files_through_mpv()
    {
        using var r = new Rig(discEngine: true);
        var disc = DvdFolder();
        await r.Vm.OpenInputAsync(disc);
        Assert.True(r.Vm.IsDiscMode);
        Assert.True(r.Vm.CanShowDiscMenu);
        Assert.Contains($"OpenDisc dvd {disc}", r.DiscPlayer!.Calls);
        Assert.DoesNotContain(r.Player.Calls, c => c.StartsWith("OpenDisc"));

        await r.Vm.OpenInputAsync(r.MediaFile());
        Assert.False(r.Vm.CanShowDiscMenu);
        Assert.False(r.Vm.IsDiscMode);
        Assert.Contains("Stop", r.DiscPlayer.Calls); // the disc stops when a file takes over
        Assert.Contains(r.Player.Calls, c => c.StartsWith("Open ") && c.EndsWith("movie.mkv"));
    }

    [Fact]
    public async Task A_disc_opened_at_startup_waits_for_the_disc_surface()
    {
        using var r = new Rig(attach: false, discEngine: true);
        var disc = DvdFolder();
        await r.Vm.OpenInputAsync(disc);   // command line: before any surface exists
        r.Vm.AttachPlayer(42);             // mpv's surface first…
        r.Vm.AttachDiscSurface(43);        // …then the disc surface: now it opens, with menus
        await r.Vm.PendingOpenTask;
        Assert.True(r.Vm.IsDiscMode);
        Assert.Contains($"OpenDisc dvd {disc}", r.DiscPlayer!.Calls);
        Assert.DoesNotContain(r.Player.Calls, c => c.StartsWith("OpenDisc"));
    }

    [Fact]
    public async Task Without_a_disc_engine_discs_still_play_through_mpv()
    {
        using var r = new Rig();
        var disc = DvdFolder();
        await r.Vm.OpenInputAsync(disc);
        Assert.False(r.Vm.IsDiscMode);
        Assert.Contains($"OpenDisc dvd {disc}", r.Player.Calls);
    }

    [Fact]
    public async Task On_a_disc_menu_the_arrows_and_enter_move_through_it()
    {
        using var r = new Rig(discEngine: true);
        await r.Vm.OpenInputAsync(DvdFolder());
        r.DiscPlayer!.IsInDiscMenu = true;
        foreach (var k in new[] { "DOWN", "UP", "RIGHT", "LEFT", "ENTER" }) r.Vm.HandleKey(k);
        Assert.Equal(["NavigateMenu Down", "NavigateMenu Up", "NavigateMenu Right", "NavigateMenu Left", "NavigateMenu Activate"],
            r.DiscPlayer.Calls.Where(c => c.StartsWith("NavigateMenu")));
        Assert.DoesNotContain(r.DiscPlayer.Calls, c => c.StartsWith("Seek"));

        // During the film the same keys seek and change the volume again.
        r.DiscPlayer.IsInDiscMenu = false;
        r.Vm.HandleKey("RIGHT");
        Assert.Contains(r.DiscPlayer.Calls, c => c.StartsWith("SeekRelative"));
    }

    [Fact]
    public async Task Menu_button_returns_to_the_disc_menu_and_menu_clicks_arent_double_clicks()
    {
        using var r = new Rig(discEngine: true);
        await r.Vm.OpenInputAsync(DvdFolder());
        r.Vm.ShowDiscMenuCommand.Execute(null);
        Assert.Contains("ShowDiscMenu", r.DiscPlayer!.Calls);

        var requests = new List<bool?>();
        r.Vm.FullscreenRequested += requests.Add;
        r.DiscPlayer.IsInDiscMenu = true;
        r.Vm.HandleClick(DateTime.UtcNow);
        r.Vm.HandleClick(DateTime.UtcNow);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Open_disc_plays_the_chosen_drive_or_folder()
    {
        using var r = new Rig(discEngine: true);
        r.Dialogs.NextDisc = DvdFolder();
        await r.Vm.OpenDiscCommand.ExecuteAsync(null);
        Assert.Contains($"OpenDisc dvd {r.Dialogs.NextDisc}", r.DiscPlayer!.Calls);

        r.Dialogs.NextDisc = Path.GetTempPath(); // a folder that isn't a disc
        await r.Vm.OpenDiscCommand.ExecuteAsync(null);
        Assert.Contains("isn't a disc", r.Vm.StatusMessage);
    }

    [Fact]
    public async Task Open_disc_plays_a_cd_rip_folder_from_its_cue_sheet()
    {
        using var r = new Rig(discEngine: true);
        var rip = Path.Combine(r.Root, "Dangerous");
        Directory.CreateDirectory(rip);
        File.WriteAllText(Path.Combine(rip, "01 - Jam.flac"), "");
        File.WriteAllText(Path.Combine(rip, "Dangerous.cue"), "FILE \"01 - Jam.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");
        r.Dialogs.NextDisc = rip;
        await r.Vm.OpenDiscCommand.ExecuteAsync(null);
        // Through mpv, as an EDL of the tracks' files (titled chapters), under the album's name.
        Assert.Contains(r.Player.Calls, c => c.StartsWith("Open edl://") && c.Contains("01 - Jam.flac") && c.EndsWith(" Dangerous"));
        Assert.False(r.Vm.IsDiscMode);
    }

    [Fact]
    public async Task An_audio_cd_plays_through_the_disc_engine_without_a_menu_button()
    {
        if (!OperatingSystem.IsWindows()) return; // a drive root with Track01.cda is Windows' view of a CD
        using var r = new Rig(discEngine: true);
        var drive = Path.Combine(r.Root, "cd");
        Directory.CreateDirectory(drive);
        File.WriteAllText(Path.Combine(drive, "Track01.cda"), "");
        await r.Vm.OpenInputAsync(drive);
        Assert.Contains($"OpenAudioCd {drive}", r.DiscPlayer!.Calls);
        Assert.True(r.Vm.IsDiscMode);
        Assert.False(r.Vm.CanShowDiscMenu);
    }

    [Fact]
    public void Linux_drives_open_from_their_mount_point_or_the_raw_drive()
    {
        var mounts = Path.Combine(Path.GetTempPath(), "paramaja-app-tests", Guid.NewGuid().ToString("N") + ".mounts");
        Directory.CreateDirectory(Path.GetDirectoryName(mounts)!);
        File.WriteAllText(mounts, "/dev/sr0 /run/media/me/MY\\040MOVIE udf ro 0 0\n/dev/sda1 / ext4 rw 0 0\n");
        var drives = OpticalDrives.Linux(mounts, ["/dev/sr1", "/dev/sr0"], _ => DiscContent.Data);
        Assert.Equal(new OpticalDrive("/run/media/me/MY MOVIE", "sr0", "MY MOVIE"), drives[0]);
        Assert.Equal("/dev/sr1", drives[1].Root); // not mounted: the raw drive
        Assert.True(drives[1].HasDisc);
    }

    [Fact]
    public void Linux_drives_tell_audio_cds_and_empty_drives_apart()
    {
        var drives = OpticalDrives.Linux("/nonexistent", ["/dev/sr0", "/dev/sr1"],
            dev => dev == "/dev/sr0" ? DiscContent.Audio : DiscContent.NoDisc);
        Assert.Equal(new OpticalDrive("/dev/sr0", "sr0", "Audio CD"), drives[0]);
        Assert.Equal("sr0 — Audio CD", drives[0].Description);
        Assert.False(drives[1].HasDisc); // nothing to play: its Play button is disabled
        Assert.Equal("sr1 — no disc", drives[1].Description);
    }
}
