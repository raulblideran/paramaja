namespace Paramaja.Core.Playback;

/// <summary>One reading of mpv's playback statistics (cumulative counters, taken about once a second).</summary>
public sealed record HealthSample(
    DateTime At,
    bool Playing,
    bool Buffering,
    long? FrameDrops,
    long? DecoderDrops,
    long? DelayedFrames,
    double? InputBytesPerSecond,
    double? CachedSeconds,
    long? FileSize,
    double Duration);

public enum Finding
{
    NoHardwareDecoding,
    DecoderTooSlow,
    RenderingTooSlow,
    NetworkTooSlow,
    Buffering,
}

/// <summary>
/// Watches the last few seconds of playback and says why it's struggling, if it is:
/// the decoder, the GPU's rendering, or a download slower than the video needs.
/// </summary>
public sealed class PlaybackHealth
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    /// <summary>Dropping at least this many frames per second while playing counts as struggling.</summary>
    public const double DropThreshold = 1.0;
    /// <summary>How much playing time the window needs before drop rates mean anything.</summary>
    public static readonly TimeSpan MinPlayingTime = TimeSpan.FromSeconds(5);

    private readonly List<HealthSample> _samples = [];
    private bool _wasBuffering;

    /// <summary>Times playback stopped to buffer since the file started.</summary>
    public int Stalls { get; private set; }
    public HealthSample? Latest => _samples.Count > 0 ? _samples[^1] : null;

    public void Reset()
    {
        _samples.Clear();
        Stalls = 0;
        _wasBuffering = false;
    }

    public void Add(HealthSample s)
    {
        if (s.Buffering && !_wasBuffering) Stalls++;
        _wasBuffering = s.Buffering;
        _samples.Add(s);
        _samples.RemoveAll(x => s.At - x.At > Window);
    }

    private TimeSpan PlayingTime()
    {
        var t = TimeSpan.Zero;
        for (var i = 1; i < _samples.Count; i++)
            if (_samples[i].Playing && _samples[i - 1].Playing) t += _samples[i].At - _samples[i - 1].At;
        return t;
    }

    /// <summary>Counter increase per second of playing time across the window.</summary>
    private double Rate(Func<HealthSample, long?> counter)
    {
        var playing = PlayingTime().TotalSeconds;
        if (playing <= 0) return 0;
        long increase = 0;
        for (var i = 1; i < _samples.Count; i++)
        {
            if (!(_samples[i].Playing && _samples[i - 1].Playing)) continue;
            if (counter(_samples[i]) is { } now && counter(_samples[i - 1]) is { } before && now >= before)
                increase += now - before;
        }
        return increase / playing;
    }

    public double RenderDropsPerSecond => Rate(s => s.FrameDrops);
    public double DecoderDropsPerSecond => Rate(s => s.DecoderDrops);

    public double? InputBytesPerSecond
    {
        get
        {
            var speeds = _samples.Select(s => s.InputBytesPerSecond).OfType<double>().ToList();
            return speeds.Count == 0 ? null : speeds.Average();
        }
    }

    /// <summary>The average bitrate the file needs, from its size and length.</summary>
    public double? RequiredBytesPerSecond =>
        Latest is { FileSize: > 0, Duration: > 0 } s ? s.FileSize!.Value / s.Duration : null;

    private bool EnoughPlaying => PlayingTime() >= MinPlayingTime;

    /// <summary>The GPU can't render frames in time: the case Fast rendering fixes.</summary>
    public bool RenderingStruggling => EnoughPlaying && RenderDropsPerSecond >= DropThreshold;

    public IReadOnlyList<Finding> Findings(string? hwdec, string? codec, int? height)
    {
        var found = new List<Finding>();
        var heavy = codec is "hevc" or "av1" or "vp9" && height >= 1440;
        if (heavy && (string.IsNullOrEmpty(hwdec) || hwdec == "no")) found.Add(Finding.NoHardwareDecoding);
        if (EnoughPlaying && DecoderDropsPerSecond >= DropThreshold) found.Add(Finding.DecoderTooSlow);
        if (RenderingStruggling) found.Add(Finding.RenderingTooSlow);
        if (Stalls > 0)
        {
            var slow = InputBytesPerSecond is { } got && RequiredBytesPerSecond is { } need && got < need * 1.1;
            found.Add(slow ? Finding.NetworkTooSlow : Finding.Buffering);
        }
        return found;
    }

    public string Explain(Finding f) => f switch
    {
        Finding.NoHardwareDecoding =>
            "Hardware decoding isn't active, so this 4K video is being decoded by the CPU. " +
            "Check Settings → Video → Hardware acceleration; older GPUs can't decode 10-bit HEVC.",
        Finding.DecoderTooSlow =>
            $"The decoder can't keep up ({DecoderDropsPerSecond:0.0} frames/s dropped before display).",
        Finding.RenderingTooSlow =>
            $"The graphics card can't keep up with rendering ({RenderDropsPerSecond:0.0} frames/s dropped). " +
            "Settings → Video → Rendering → Fast fixes this.",
        Finding.NetworkTooSlow =>
            $"The download ({Mb(InputBytesPerSecond)}/s) is slower than this video needs ({Mb(RequiredBytesPerSecond)}/s), " +
            $"so it stopped to buffer {Stalls} time(s). Paramaja buffers longer after each stall.",
        Finding.Buffering => $"Playback stopped to buffer {Stalls} time(s).",
        _ => f.ToString(),
    };

    private static string Mb(double? bytesPerSecond) =>
        bytesPerSecond is { } b ? $"{b / 1_000_000:0.0} MB" : "? MB";
}
