namespace NegsPlayer.Core.Input;

public enum InputKind { None, Magnet, Url, File }

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
            if (uri.IsFile) return File.Exists(uri.LocalPath) ? (InputKind.File, uri.LocalPath) : (InputKind.None, "");
            if (uri.Scheme is "http" or "https" or "rtsp" or "rtmp" or "rtp" or "udp" or "srt" or "smb" or "ftp")
                return (InputKind.Url, t);
        }

        return File.Exists(t) ? (InputKind.File, Path.GetFullPath(t)) : (InputKind.None, "");
    }
}
