namespace NegsPlayer.Core.Settings;

/// <summary>
/// Everything the app writes lives next to the executable, so the folder can be
/// copied around (USB stick, Downloads, ...) and nothing touches AppData or the registry.
/// </summary>
public sealed class PortablePaths
{
    public PortablePaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public static PortablePaths ForApp() => new(AppContext.BaseDirectory);

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string Screenshots => Path.Combine(Root, "Screenshots");
    public string TorrentCache => Path.Combine(Root, "cache");
    /// <summary>mpv's own config dir (mpv.conf, input.conf, shaders) — optional power-user hook.</summary>
    public string MpvConfig => Path.Combine(Root, "portable_config");
    public string Logs => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Screenshots, TorrentCache, MpvConfig }) Directory.CreateDirectory(d);
    }
}
