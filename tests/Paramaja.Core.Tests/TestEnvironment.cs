using Paramaja.Core.Mpv;
using Paramaja.Core.Native;

namespace Paramaja.Core.Tests;

/// <summary>Locates fixtures and native libraries; integration tests skip when they're missing.</summary>
public static class TestEnvironment
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Media(string name) => Path.Combine(RepoRoot, "tests", "fixtures", "media", name);

    public static string TorrentBuildDir => Path.Combine(RepoRoot, "native", "paramaja-torrent", "target", "debug");

    public static string SeedExample => Path.Combine(TorrentBuildDir, "examples", OperatingSystem.IsWindows() ? "seed.exe" : "seed");

    static TestEnvironment()
    {
        NativeLibraries.SearchDirectories.Add(TorrentBuildDir);
        NativeLibraries.SearchDirectories.Add(Path.Combine(RepoRoot, "libs"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Paramaja.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static readonly Lazy<bool> _mpv = new(() =>
    {
        try { return MpvClient.ApiVersion > 0; }
        catch (DllNotFoundException) { return false; }
    });

    public static bool HasMpv => _mpv.Value;
    public static bool HasFixtures => File.Exists(Media("sdr_h264.mp4"));

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "paramaja-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>Fact that is skipped when libmpv or the generated fixtures are unavailable.</summary>
public sealed class MpvFactAttribute : FactAttribute
{
    public MpvFactAttribute()
    {
        if (!TestEnvironment.HasMpv) Skip = "libmpv not found";
        else if (!TestEnvironment.HasFixtures) Skip = "run tests/fixtures/make-fixtures.sh first";
    }
}

public sealed class MpvTheoryAttribute : TheoryAttribute
{
    public MpvTheoryAttribute()
    {
        if (!TestEnvironment.HasMpv) Skip = "libmpv not found";
        else if (!TestEnvironment.HasFixtures) Skip = "run tests/fixtures/make-fixtures.sh first";
    }
}
