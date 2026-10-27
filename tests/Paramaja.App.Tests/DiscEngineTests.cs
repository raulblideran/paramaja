using Paramaja.App.Services;
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
        Assert.Contains($"OpenDisc dvd {disc}", r.DiscPlayer!.Calls);
        Assert.DoesNotContain(r.Player.Calls, c => c.StartsWith("OpenDisc"));

        await r.Vm.OpenInputAsync(r.MediaFile());
        Assert.False(r.Vm.IsDiscMode);
        Assert.Contains("Stop", r.DiscPlayer.Calls); // the disc stops when a file takes over
        Assert.Contains(r.Player.Calls, c => c.StartsWith("Open ") && c.EndsWith("movie.mkv"));
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
        Assert.Contains("isn't a DVD or Blu-ray", r.Vm.StatusMessage);
    }

    [Fact]
    public void Linux_drives_open_from_their_mount_point_or_the_raw_drive()
    {
        var mounts = Path.Combine(Path.GetTempPath(), "paramaja-app-tests", Guid.NewGuid().ToString("N") + ".mounts");
        Directory.CreateDirectory(Path.GetDirectoryName(mounts)!);
        File.WriteAllText(mounts, "/dev/sr0 /run/media/me/MY\\040MOVIE udf ro 0 0\n/dev/sda1 / ext4 rw 0 0\n");
        var drives = OpticalDrives.Linux(mounts, ["/dev/sr1", "/dev/sr0"]);
        Assert.Equal(new OpticalDrive("/run/media/me/MY MOVIE", "sr0", "MY MOVIE"), drives[0]);
        Assert.Equal("/dev/sr1", drives[1].Root); // not mounted: the raw drive
        Assert.True(drives[1].HasDisc);
    }
}
