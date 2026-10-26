namespace Paramaja.Core.Playback;

/// <summary>
/// Logarithmic volume: the slider moves in equal steps of loudness (decibels), the way a
/// hardware volume knob does, instead of equal steps of mpv's volume value.
///
/// Slider 0-100 spans -60 dB to 0 dB linearly; 100-150 adds up to mpv's +10.6 dB boost.
/// mpv applies its volume property cubically (gain = (v/100)^3), so the matching mpv
/// value is 100 · 10^(dB/60).
/// </summary>
public static class VolumeCurve
{
    public const double Max = 150;
    public const double FloorDb = -60;
    /// <summary>mpv volume 150 = 1.5³ gain ≈ +10.57 dB.</summary>
    public static readonly double MaxBoostDb = 60 * Math.Log10(1.5);

    public static double ToDecibels(double level)
    {
        level = Math.Clamp(level, 0, Max);
        if (level <= 0) return double.NegativeInfinity;
        return level <= 100
            ? FloorDb * (1 - level / 100)
            : (level - 100) / 50 * MaxBoostDb;
    }

    /// <summary>Slider position → mpv's <c>volume</c> property.</summary>
    public static double ToMpv(double level)
    {
        var db = ToDecibels(level);
        return double.IsNegativeInfinity(db) ? 0 : 100 * Math.Pow(10, db / 60);
    }

    /// <summary>mpv's <c>volume</c> property → slider position.</summary>
    public static double FromMpv(double mpvVolume)
    {
        if (mpvVolume <= 0) return 0;
        var db = 60 * Math.Log10(mpvVolume / 100);
        var level = db <= 0 ? 100 * (1 + db / -FloorDb) : 100 + db / MaxBoostDb * 50;
        return Math.Round(Math.Clamp(level, 0, Max), 2);
    }

    public static string Describe(double level)
    {
        var db = ToDecibels(level);
        return double.IsNegativeInfinity(db) ? "muted" : $"{db:+0.0;-0.0;0} dB";
    }
}
