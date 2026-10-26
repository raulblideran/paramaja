using System.Reflection;
using System.Text.RegularExpressions;

namespace Paramaja.App.Services;

/// <summary>
/// The version stamped into the exe: <c>1.1.0+50919f7</c> becomes "1.1.0 (50919f7)", so
/// friends sharing builds can say exactly which one they're running.
/// </summary>
public static partial class AppInfo
{
    public const string Name = "Paramaja";
    public const string Homepage = "https://github.com/raulblideran/paramaja";

    private static readonly (string Version, string? Commit) Current = Parse(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Version => Current.Version;
    public static string? Commit => Current.Commit;
    public static string Display => Commit is null ? Version : $"{Version} ({Commit})";

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex FullSha();

    /// <summary>Splits "1.1.0+commit". Full 40-character hashes are shortened to 7.</summary>
    public static (string Version, string? Commit) Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return ("0.0.0", null);
        var parts = informational.Split('+', 2);
        var commit = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        if (commit is not null && FullSha().IsMatch(commit)) commit = commit[..7];
        return (parts[0], commit);
    }
}
