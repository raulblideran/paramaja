using Avalonia.Input;
using Paramaja.Core.Input;

namespace Paramaja.App.Services;

/// <summary>Converts Avalonia key events into the canonical names used by <see cref="ShortcutMap"/>.</summary>
public static class KeyMapping
{
    public static string? ToCanonical(Key key, KeyModifiers mods, string? symbol = null)
    {
        var ctrl = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var alt = mods.HasFlag(KeyModifiers.Alt);

        var name = key switch
        {
            Key.Space => "SPACE",
            Key.Right => "RIGHT",
            Key.Left => "LEFT",
            Key.Up => "UP",
            Key.Down => "DOWN",
            Key.Escape => "ESC",
            Key.Enter => "ENTER",
            Key.PageUp => "PGUP",
            Key.PageDown => "PGDWN",
            Key.MediaPlayPause => "PLAYPAUSE",
            Key.OemPeriod or Key.Decimal => ".",
            Key.OemComma => ",",
            >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            _ => symbol is { Length: 1 } ? symbol : null,
        };
        return name is null ? null : ShortcutMap.Canonical(name, ctrl, shift, alt);
    }

    public static string? ToCanonical(KeyEventArgs e) => ToCanonical(e.Key, e.KeyModifiers, e.KeySymbol);
}
