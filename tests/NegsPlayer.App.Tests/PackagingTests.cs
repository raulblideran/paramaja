using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NegsPlayer.App.Tests;

/// <summary>
/// The Flatpak manifest, desktop entry and metainfo are text that nothing compiles, so a typo
/// in one only shows up as an app that installs without an icon or never opens magnet links.
/// These tests read them and hold them to each other and to the project.
/// </summary>
public class PackagingTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Packaging = Path.Combine(Root, "packaging");
    private const string Id = Program.AppId;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NegsPlayer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(Packaging, name));
    private static string Manifest => Read($"{Id}.yaml");

    private static Dictionary<string, string> DesktopEntry() =>
        Read($"{Id}.desktop").Split('\n')
            .Where(l => l.Contains('=') && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1].Trim());

    private static string ProjectVersion() =>
        Regex.Match(File.ReadAllText(Path.Combine(Root, "src", "NegsPlayer.App", "NegsPlayer.App.csproj")),
            "<Version>(.*?)</Version>").Groups[1].Value;

    [Fact]
    public void Every_packaging_file_is_named_after_the_app_id()
    {
        foreach (var ext in new[] { "yaml", "desktop", "metainfo.xml", "svg" })
            Assert.True(File.Exists(Path.Combine(Packaging, $"{Id}.{ext}")), $"{Id}.{ext} missing");
        Assert.Contains($"app-id: {Id}", Manifest);
    }

    [Fact]
    public void Manifest_grants_what_playback_and_torrents_need()
    {
        var m = Manifest;
        foreach (var arg in new[] { "--socket=x11", "--device=dri", "--socket=pulseaudio", "--share=network",
                     "--filesystem=host:ro", "--filesystem=xdg-pictures" })
            Assert.Contains($"- {arg}", m);
        Assert.Contains("command: negs-player", m);
    }

    [Fact]
    public void Every_downloaded_archive_is_pinned_by_hash_and_every_git_source_by_commit()
    {
        var lines = Manifest.Split('\n').Select(l => l.Trim()).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] == "- type: archive")
                Assert.StartsWith("sha256: ", lines[i + 2]);
            if (lines[i] == "- type: git")
                Assert.Contains(lines.Skip(i + 1).Take(3), l => Regex.IsMatch(l, "^commit: [0-9a-f]{40}$"));
        }
    }

    [Fact]
    public void Wrapper_launches_the_folder_the_app_is_published_to()
    {
        var wrapper = Read(Path.Combine("flatpak", "negs-player.sh"));
        Assert.Contains("exec /app/lib/negs-player/NegsPlayer \"$@\"", wrapper);
        Assert.Contains("-o /app/lib/negs-player", Manifest);
        // The torrent engine has to sit beside the exe, where NativeLibraries looks first.
        Assert.Contains("libnegs_torrent.so -t /app/lib/negs-player/", Manifest);
    }

    [Fact]
    public void Desktop_entry_opens_files_and_magnet_links()
    {
        var d = DesktopEntry();
        Assert.Equal("negs-player %U", d["Exec"]);
        Assert.Equal(Id, d["Icon"]);
        Assert.Equal(Id, d["StartupWMClass"]);
        var mime = d["MimeType"].Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("x-scheme-handler/magnet", mime);
        Assert.Contains("video/x-matroska", mime);
        Assert.Contains("video/mp4", mime);
    }

    [Fact]
    public void Metainfo_matches_the_app_and_the_current_version()
    {
        var x = XDocument.Load(Path.Combine(Packaging, $"{Id}.metainfo.xml")).Root!;
        Assert.Equal(Id, x.Element("id")!.Value);
        Assert.Equal($"{Id}.desktop", x.Element("launchable")!.Value);
        Assert.Equal("GPL-3.0-or-later", x.Element("project_license")!.Value);
        var latest = x.Element("releases")!.Elements("release").First();
        Assert.Equal(ProjectVersion(), latest.Attribute("version")!.Value);
    }

    [Fact]
    public void Packaged_icon_is_the_app_icon()
    {
        Assert.Equal(File.ReadAllText(Path.Combine(Root, "src", "NegsPlayer.App", "Assets", "negs.svg")), Read($"{Id}.svg"));
        Assert.True(File.Exists(Path.Combine(Root, "LICENSE")));
    }
}
