using System.Collections.ObjectModel;

namespace Paramaja.Core.Input;

/// <summary>
/// Keyboard/mouse shortcuts, keyed by mpv-style key names ("SPACE", "Ctrl+v", "S", ".").
/// The same names are used both for keys pressed in the Avalonia window and for keys
/// mpv receives while its video surface has focus, so behaviour is identical either way.
/// </summary>
public static class ShortcutMap
{
    public static ReadOnlyDictionary<string, PlayerAction> Default { get; } = new(new Dictionary<string, PlayerAction>
    {
        ["SPACE"] = PlayerAction.TogglePause,
        ["PLAYPAUSE"] = PlayerAction.TogglePause,
        ["RIGHT"] = PlayerAction.SeekForward,
        ["LEFT"] = PlayerAction.SeekBackward,
        ["Ctrl+RIGHT"] = PlayerAction.SeekForwardLong,
        ["Ctrl+LEFT"] = PlayerAction.SeekBackwardLong,
        ["."] = PlayerAction.FrameStep,
        [","] = PlayerAction.FrameBackStep,
        ["PGDWN"] = PlayerAction.NextChapter,
        ["PGUP"] = PlayerAction.PreviousChapter,
        ["UP"] = PlayerAction.VolumeUp,
        ["DOWN"] = PlayerAction.VolumeDown,
        ["WHEEL_UP"] = PlayerAction.VolumeUp,
        ["WHEEL_DOWN"] = PlayerAction.VolumeDown,
        ["m"] = PlayerAction.ToggleMute,
        ["f"] = PlayerAction.ToggleFullscreen,
        ["MBTN_LEFT_DBL"] = PlayerAction.ToggleFullscreen,
        ["ENTER"] = PlayerAction.ToggleFullscreen,
        ["ESC"] = PlayerAction.ExitFullscreen,
        ["s"] = PlayerAction.Screenshot,
        ["S"] = PlayerAction.ScreenshotWithSubtitles,
        ["d"] = PlayerAction.ToggleDeinterlace,
        ["i"] = PlayerAction.ToggleStats,
        ["h"] = PlayerAction.ToggleHdrMode,
        ["D"] = PlayerAction.ToggleDownmix,
        ["a"] = PlayerAction.CycleAudioTrack,
        ["j"] = PlayerAction.CycleSubtitleTrack,
        ["v"] = PlayerAction.ToggleSubtitles,
        ["z"] = PlayerAction.SubtitleDelayDown,
        ["x"] = PlayerAction.SubtitleDelayUp,
        ["Ctrl+o"] = PlayerAction.OpenFile,
        ["Ctrl+O"] = PlayerAction.OpenFolder,
        ["Ctrl+u"] = PlayerAction.OpenLink,
        ["Ctrl+v"] = PlayerAction.PasteAndPlay,
        ["Ctrl+s"] = PlayerAction.Stop,
        ["Ctrl+i"] = PlayerAction.CopyDiagnostics,
    });

    /// <summary>
    /// Builds a canonical key name. <paramref name="key"/> is either a named key ("SPACE",
    /// "RIGHT") or a single printable character. Shift on a letter is expressed by case.
    /// </summary>
    public static string Canonical(string key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        if (key.Length == 1 && char.IsLetter(key[0]))
        {
            key = shift ? key.ToUpperInvariant() : key.ToLowerInvariant();
            shift = false;
        }
        else if (key.Length == 1)
        {
            shift = false; // punctuation already reflects shift (e.g. '<')
        }
        var prefix = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "");
        return prefix + key;
    }

    public static bool TryGet(string canonicalKey, out PlayerAction action) =>
        Default.TryGetValue(canonicalKey, out action);

    /// <summary>Human-readable shortcut list for the help/settings panel.</summary>
    public static IEnumerable<(string Keys, PlayerAction Action)> Describe() =>
        Default.GroupBy(kv => kv.Value)
            .Select(g => (string.Join(" / ", g.Select(kv => Pretty(kv.Key))), g.Key));

    private static string Pretty(string key) => key switch
    {
        "SPACE" => "Space",
        "RIGHT" => "→",
        "LEFT" => "←",
        "UP" => "↑",
        "DOWN" => "↓",
        "Ctrl+RIGHT" => "Ctrl+→",
        "Ctrl+LEFT" => "Ctrl+←",
        "WHEEL_UP" => "Wheel ↑",
        "WHEEL_DOWN" => "Wheel ↓",
        "MBTN_LEFT_DBL" => "Double-click",
        "PLAYPAUSE" => "Media Play",
        "ESC" => "Esc",
        "ENTER" => "Enter",
        "PGUP" => "Page Up",
        "PGDWN" => "Page Down",
        "S" => "Shift+S",
        "D" => "Shift+D",
        "Ctrl+O" => "Ctrl+Shift+O",
        _ when key.StartsWith("Ctrl+") && key.Length == 6 => "Ctrl+" + char.ToUpperInvariant(key[5]),
        _ when key.Length == 1 => key.ToUpperInvariant(),
        _ => key,
    };
}
