using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Paramaja.App.Services;

namespace Paramaja.App.Tests;

public class VersionTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Paramaja.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string ProjectVersion(string root) =>
        Regex.Match(File.ReadAllText(Path.Combine(root, "src", "Paramaja.App", "Paramaja.App.csproj")), "<Version>(.*?)</Version>").Groups[1].Value;

    [Theory]
    [InlineData("1.1.0+50919f7", "1.1.0", "50919f7")]
    [InlineData("1.1.0+50919f7-dirty", "1.1.0", "50919f7-dirty")]
    [InlineData("1.1.0+50919f7c0f7d1e2a3b4c5d6e7f8091a2b3c4d5e6", "1.1.0", "50919f7")]
    [InlineData("1.1.0", "1.1.0", null)]
    [InlineData("1.1.0+", "1.1.0", null)]
    [InlineData(null, "0.0.0", null)]
    public void Informational_version_is_split_into_version_and_commit(string? info, string version, string? commit) =>
        Assert.Equal((version, commit), AppInfo.Parse(info));

    [Fact]
    public void The_running_build_reports_the_project_version()
    {
        Assert.Equal(ProjectVersion(Root), AppInfo.Version);
        Assert.StartsWith(AppInfo.Version, AppInfo.Display);
    }

    [Fact]
    public void Changelog_and_metainfo_lead_with_the_project_version()
    {
        var version = ProjectVersion(Root);
        var changelog = File.ReadAllText(Path.Combine(Root, "CHANGELOG.md"));
        Assert.Equal(version, Regex.Match(changelog, @"^## (\S+)", RegexOptions.Multiline).Groups[1].Value);
        var meta = XDocument.Load(Path.Combine(Root, "packaging", "io.github.raulblideran.Paramaja.metainfo.xml"));
        Assert.Equal(version, meta.Root!.Element("releases")!.Elements("release").First().Attribute("version")!.Value);
    }

    [Fact]
    public void Publish_script_names_the_zip_after_the_version_and_stamps_the_commit()
    {
        var script = File.ReadAllText(Path.Combine(Root, "build", "publish-win.sh"));
        Assert.Contains("ZIP=\"Paramaja-$VERSION-portable-win-x64.zip\"", script);
        Assert.Contains("-p:SourceRevisionId=\"$COMMIT\"", script);
        Assert.Contains("CHANGELOG.txt", script);
    }

    [Fact]
    public void Bump_script_updates_every_place_the_version_lives()
    {
        if (OperatingSystem.IsWindows()) return; // bash script
        var tmp = Path.Combine(Path.GetTempPath(), "paramaja-bump", Guid.NewGuid().ToString("N"));
        foreach (var rel in new[] { "build/bump-version.sh", "CHANGELOG.md", "src/Paramaja.App/Paramaja.App.csproj",
                     "packaging/io.github.raulblideran.Paramaja.metainfo.xml" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tmp, rel))!);
            File.Copy(Path.Combine(Root, rel), Path.Combine(tmp, rel));
        }
        var p = Process.Start(new ProcessStartInfo("bash", $"\"{Path.Combine(tmp, "build", "bump-version.sh")}\" 9.8.7 \"Tested & <escaped>\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit(30_000);
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());

        Assert.Equal("9.8.7", ProjectVersion(tmp));
        var changelog = File.ReadAllText(Path.Combine(tmp, "CHANGELOG.md"));
        Assert.Equal("9.8.7", Regex.Match(changelog, @"^## (\S+)", RegexOptions.Multiline).Groups[1].Value);
        Assert.Contains("- Tested & <escaped>", changelog);
        var meta = XDocument.Load(Path.Combine(tmp, "packaging", "io.github.raulblideran.Paramaja.metainfo.xml"));
        var latest = meta.Root!.Element("releases")!.Elements("release").First();
        Assert.Equal("9.8.7", latest.Attribute("version")!.Value);
        Assert.Equal("Tested & <escaped>", latest.Element("description")!.Element("p")!.Value);

        var bad = Process.Start(new ProcessStartInfo("bash", $"\"{Path.Combine(tmp, "build", "bump-version.sh")}\" v2") { RedirectStandardError = true })!;
        bad.WaitForExit(30_000);
        Assert.NotEqual(0, bad.ExitCode);
    }
}
