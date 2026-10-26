namespace Paramaja.Core.Input;

public enum InputKind { None, Magnet, Url, File, Mega }

/// <summary>Decides what a pasted/dropped/command-line string is.</summary>
public static class InputClassifier
{
    public static (InputKind Kind, string Value) Classify(string? text)
    {
        var t = text?.Trim().Trim('"') ?? "";
        if (t.Length == 0) return (InputKind.None, "");

        if (t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return (InputKind.Magnet, t);
        if (t.Length == 40 && t.All(Uri.IsHexDigit)) return (InputKind.Magnet, $"magnet:?xt=urn:btih:{t.ToLowerInvariant()}");

        if (Uri.TryCreate(t, UriKind.Absolute, out var uri))
        {
            // MEGA files are encrypted; they go through the engine, which decrypts them.
            if (uri.Scheme is "http" or "https" && IsMegaHost(uri.Host)) return (InputKind.Mega, t);
            if (uri.IsFile) return File.Exists(uri.LocalPath) ? (InputKind.File, uri.LocalPath) : (InputKind.None, "");
            if (uri.Scheme is "http" or "https" or "rtsp" or "rtmp" or "rtp" or "udp" or "srt" or "smb" or "ftp")
                return (InputKind.Url, t);
        }

        return File.Exists(t) ? (InputKind.File, Path.GetFullPath(t)) : (InputKind.None, "");
    }

    private static bool IsMegaHost(string host) =>
        host is "mega.nz" or "www.mega.nz" or "mega.co.nz" or "www.mega.co.nz" or "mega.io";

    /// <summary>Streams that should buffer in bigger chunks (everything that isn't a local file).</summary>
    public static bool IsStreaming(InputKind kind) => kind is InputKind.Url or InputKind.Magnet or InputKind.Mega;
}
