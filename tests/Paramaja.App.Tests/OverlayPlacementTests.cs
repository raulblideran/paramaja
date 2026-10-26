using Avalonia;
using Paramaja.App.Views;

namespace Paramaja.App.Tests;

public class OverlayPlacementTests
{
    [Fact]
    public void Visible_bars_sit_at_the_top_and_bottom_of_the_video()
    {
        var area = OverlayPlacement.PlayerArea(new PixelPoint(100, 50), new Size(1280, 720), 1.5, null, fullscreen: false);
        var (top, bottom) = OverlayPlacement.Place(area, bottomBarHeight: 118, scale: 1.5, visible: true);
        Assert.Equal(new PixelPoint(100, 50), top);
        Assert.Equal(new PixelPoint(100, 50 + (int)Math.Round(720 * 1.5) - (int)Math.Round(118 * 1.5)), bottom);
    }

    [Fact]
    public void In_fullscreen_the_bottom_bar_sits_on_the_monitors_bottom_edge()
    {
        var monitor = new PixelRect(2560, 0, 2560, 1440);
        // The video control still reports a size short of the monitor (stale after the switch).
        var area = OverlayPlacement.PlayerArea(new PixelPoint(2560, 0), new Size(2048, 1116), 1.25, monitor, fullscreen: true);
        Assert.Equal(monitor, area);
        var (top, bottom) = OverlayPlacement.Place(area, bottomBarHeight: 118, scale: 1.25, visible: true);
        Assert.Equal(new PixelPoint(2560, 0), top);
        Assert.Equal(new PixelPoint(2560, 1440 - 148), bottom);
    }

    [Fact]
    public void Hidden_bars_are_parked_off_screen_not_hidden()
    {
        var (top, bottom) = OverlayPlacement.Place(new PixelRect(0, 0, 1920, 1080), 118, 1, visible: false);
        Assert.Equal(OverlayPlacement.Parked, top);
        Assert.Equal(OverlayPlacement.Parked, bottom);
        Assert.True(OverlayPlacement.Parked.X < -10000 && OverlayPlacement.Parked.Y < -10000);
    }

    [Fact]
    public void Parking_is_only_used_where_the_os_allows_off_screen_windows() =>
        Assert.Equal(OperatingSystem.IsWindows(), OverlayPlacement.ParkInsteadOfHiding);

    [Fact]
    public void In_fullscreen_the_whole_monitor_counts_as_the_player()
    {
        var monitor = new PixelRect(2560, 0, 3840, 2160);
        var staleWindowRect = new PixelRect(2600, 100, 1280, 760); // what a window may still report
        var pointer = new PixelPoint(6000, 2000);                    // bottom-right of the monitor
        Assert.False(OverlayPlacement.IsPointerOverPlayer(pointer, staleWindowRect, monitor, fullscreen: false));
        Assert.True(OverlayPlacement.IsPointerOverPlayer(pointer, staleWindowRect, monitor, fullscreen: true));
        Assert.False(OverlayPlacement.IsPointerOverPlayer(new PixelPoint(10, 10), staleWindowRect, monitor, fullscreen: true),
            "the other monitor doesn't count");
    }

    [Fact]
    public void Windowed_uses_the_client_area_in_physical_pixels()
    {
        var rect = OverlayPlacement.WindowRect(new PixelPoint(100, 100), new Size(1280, 720), 1.25);
        Assert.Equal(new PixelRect(100, 100, 1600, 900), rect);
        Assert.True(OverlayPlacement.IsPointerOverPlayer(new PixelPoint(1699, 999), rect, null, fullscreen: false));
        Assert.False(OverlayPlacement.IsPointerOverPlayer(new PixelPoint(1750, 1050), rect, null, fullscreen: false));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Bars_are_topmost_only_in_fullscreen_while_in_front(bool fullscreen, bool active, bool expected) =>
        Assert.Equal(expected, OverlayPlacement.ShouldBeTopmost(fullscreen, active));
}
