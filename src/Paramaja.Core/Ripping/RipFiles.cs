using System.Diagnostics;
using System.Text;

namespace Paramaja.Core.Ripping;

/// <summary>Where a rip is.</summary>
/// <param name="Fraction">Of the whole disc, 0 to 1.</param>
public sealed record RipProgress(string Stage, int Track, int Tracks, double Fraction, double Speed = 0, int Rereads = 0);

/// <summary>File and folder names for rips.</summary>
public static class RipNames
{
    private static readonly char[] Invalid = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// A name any system takes: no path separators or characters Windows refuses (a colon or
    /// slash becomes a dash, the rest go), no control characters, no trailing dots or spaces.
    /// </summary>
    public static string Safe(string name, int max = 120)
    {
        var b = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsControl(c)) continue;
            if (c is ':' or '/' or '\\' or '|') b.Append(" - ");
            else if (Array.IndexOf(Invalid, c) < 0) b.Append(c);
        }
        var safe = string.Join(' ', b.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace(" - - ", " - ");
        if (safe.Length > max) safe = safe[..max];
        safe = safe.TrimEnd('.', ' ');
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>A folder under <paramref name="parent"/> no rip has used: "Name", else "Name (1)" and so on.</summary>
    public static string NewFolder(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        for (var n = 1; Directory.Exists(path) || File.Exists(path); n++) path = Path.Combine(parent, $"{name} ({n})");
        return path;
    }

    /// <summary>A file name in <paramref name="folder"/> nothing has: "Name.ext", else "Name (1).ext" and so on.</summary>
    public static string NewFile(string folder, string stem, string extension)
    {
        var path = Path.Combine(folder, stem + extension);
        for (var n = 1; File.Exists(path) || Directory.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }
}

/// <summary>Finds the tools a rip runs: bundled next to the app first, then on the PATH.</summary>
public static class RipTools
{
    public static string? Find(string name, string? appDirectory = null)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var bundled = Path.Combine(appDirectory ?? AppContext.BaseDirectory, exe);
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Runs a tool to the end; throws <see cref="IOException"/> with its last words if it fails.</summary>
    public static async Task RunAsync(string exe, IEnumerable<string> arguments, CancellationToken ct,
        Action<string>? onStdout = null, Action<string>? onStderr = null)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new IOException($"couldn't start {Path.GetFileName(exe)}");
        var errors = new Queue<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            onStderr?.Invoke(e.Data);
            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > 8) errors.Dequeue();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            string last;
            lock (errors) last = string.Join(" ", errors.Where(l => l.Trim().Length > 0).TakeLast(3));
            throw new IOException($"{Path.GetFileNameWithoutExtension(exe)} failed ({process.ExitCode}): {last}");
        }
    }
}
