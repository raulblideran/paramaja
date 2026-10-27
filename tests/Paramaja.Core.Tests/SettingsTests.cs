using Paramaja.Core.Settings;

namespace Paramaja.Core.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("{ \"renderQuality\": \"Auto\" }", RenderQuality.Balanced)]                        // old default: moved once
    [InlineData("{ \"renderQuality\": \"High\" }", RenderQuality.High)]                            // picked by hand: kept
    [InlineData("{ \"settingsVersion\": 2, \"renderQuality\": \"Auto\" }", RenderQuality.Auto)]  // picked Auto after the move: kept
    public void Old_auto_rendering_moves_to_balanced_once(string json, RenderQuality expected)
    {
        var store = new SettingsStore(Path.Combine(TestEnvironment.TempDir(), "settings.json"));
        File.WriteAllText(store.FilePath, json);
        var s = store.Load();
        Assert.Equal(expected, s.RenderQuality);
        Assert.Equal(PlayerSettings.CurrentVersion, s.SettingsVersion);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(Path.Combine(TestEnvironment.TempDir(), "settings.json")).Load();
        Assert.False(s.DownmixToStereo);
        Assert.Equal(HdrMode.Auto, s.HdrMode);
        Assert.Equal(RenderQuality.Balanced, s.RenderQuality);
        Assert.Equal(60, s.DisplayFps);
        Assert.Equal(100, s.Volume);
    }

    [Fact]
    public void Round_trips_all_values()
    {
        var store = new SettingsStore(Path.Combine(TestEnvironment.TempDir(), "settings.json"));
        var s = new PlayerSettings
        {
            DownmixToStereo = true,
            HdrMode = HdrMode.Passthrough,
            ToneMapping = "bt.2446a",
            Deinterlace = DeinterlaceMode.Off,
            Volume = 77,
            ClearTorrentCacheOnExit = false,
        };
        s.AddRecent("a.mkv");
        store.Save(s);
        var loaded = store.Load();
        Assert.True(loaded.DownmixToStereo);
        Assert.Equal(HdrMode.Passthrough, loaded.HdrMode);
        Assert.Equal("bt.2446a", loaded.ToneMapping);
        Assert.Equal(DeinterlaceMode.Off, loaded.Deinterlace);
        Assert.Equal(77, loaded.Volume);
        Assert.False(loaded.ClearTorrentCacheOnExit);
        Assert.Equal(["a.mkv"], loaded.RecentItems);
    }

    [Fact]
    public void Enums_are_stored_as_readable_strings()
    {
        var path = Path.Combine(TestEnvironment.TempDir(), "settings.json");
        new SettingsStore(path).Save(new PlayerSettings { HdrMode = HdrMode.Passthrough });
        var json = File.ReadAllText(path);
        Assert.Contains("\"Passthrough\"", json);
        Assert.Contains("\"downmixToStereo\": false", json);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_used()
    {
        var path = Path.Combine(TestEnvironment.TempDir(), "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var s = new SettingsStore(path).Load();
        Assert.Equal(100, s.Volume);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Hand_edited_values_are_clamped()
    {
        var path = Path.Combine(TestEnvironment.TempDir(), "settings.json");
        File.WriteAllText(path, """{ "volume": 9000, "toneMapping": "nonsense", "seekSeconds": 0, "subtitleScale": 100, "recentItems": null, }""");
        var s = new SettingsStore(path).Load();
        Assert.Equal(150, s.Volume);
        Assert.Equal("mobius", s.ToneMapping);
        Assert.Equal(1, s.SeekSeconds);
        Assert.Equal(4, s.SubtitleScale);
        Assert.NotNull(s.RecentItems);
    }

    [Fact]
    public void Mobius_is_the_default_curve_and_the_old_default_moves_to_it_once()
    {
        Assert.Equal("mobius", new PlayerSettings().ToneMapping);
        Assert.Equal("mobius", PlayerSettings.ToneMappingCurves[0]);
        var path = Path.Combine(TestEnvironment.TempDir(), "settings.json");

        // 1.6.0 and older saved spline, the default then (and a switch that no longer exists).
        File.WriteAllText(path, """{ "settingsVersion": 2, "toneMapping": "spline", "hardwareDecoding": false }""");
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.Equal("mobius", s.ToneMapping);
        Assert.Equal(PlayerSettings.CurrentVersion, s.SettingsVersion);

        // Choosing spline afterwards sticks.
        s.ToneMapping = "spline";
        store.Save(s);
        Assert.Equal("spline", store.Load().ToneMapping);

        // Any other curve someone picked before is kept.
        File.WriteAllText(path, """{ "settingsVersion": 2, "toneMapping": "hable" }""");
        Assert.Equal("hable", new SettingsStore(path).Load().ToneMapping);
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_file()
    {
        var dir = TestEnvironment.TempDir();
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        store.Save(new PlayerSettings());
        store.Save(new PlayerSettings { Volume = 50 });
        Assert.Equal(["settings.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Recent_items_are_deduplicated_most_recent_first_and_capped()
    {
        var s = new PlayerSettings();
        for (var i = 0; i < 20; i++) s.AddRecent($"file{i}");
        s.AddRecent("file10");
        Assert.Equal(PlayerSettings.MaxRecentItems, s.RecentItems.Count);
        Assert.Equal("file10", s.RecentItems[0]);
        Assert.Single(s.RecentItems, "file10");
    }

    [Fact]
    public void Portable_paths_are_all_under_the_root()
    {
        var root = TestEnvironment.TempDir();
        var p = new PortablePaths(root);
        foreach (var path in new[] { p.SettingsFile, p.Screenshots, p.TorrentCache, p.MpvConfig, p.Logs })
            Assert.StartsWith(Path.GetFullPath(root), path);
        p.EnsureCreated();
        Assert.True(Directory.Exists(p.Screenshots));
        Assert.True(Directory.Exists(p.TorrentCache));
    }

    [Fact]
    public void Flatpak_paths_use_the_sandbox_dirs_and_the_pictures_folder()
    {
        var home = TestEnvironment.TempDir();
        var p = PortablePaths.ForFlatpak(Path.Combine(home, "config"), Path.Combine(home, "cache"), Path.Combine(home, "Pictures"));
        Assert.Equal(Path.Combine(home, "config", "settings.json"), p.SettingsFile);
        Assert.Equal(Path.Combine(home, "config", "portable_config"), p.MpvConfig);
        Assert.Equal(Path.Combine(home, "cache", "torrents"), p.TorrentCache);
        Assert.Equal(Path.Combine(home, "Pictures", "Paramaja"), p.Screenshots);
        Assert.StartsWith(Path.Combine(home, "cache"), p.Logs);
    }
}
