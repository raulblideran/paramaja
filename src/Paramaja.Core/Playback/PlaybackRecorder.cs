namespace Paramaja.Core.Playback;

/// <summary>
/// Keeps every second's playback statistics since the playing video started, for the log
/// (Create Log): play the clip, then save, and the whole run is in it. The last hour at most.
/// </summary>
public sealed class PlaybackRecorder
{
    public const int MaxSamples = 3600;
    private readonly List<HealthSample> _samples = [];

    public IReadOnlyList<HealthSample> Samples => _samples;

    public void Reset() => _samples.Clear();

    public void Add(HealthSample sample)
    {
        _samples.Add(sample);
        if (_samples.Count > MaxSamples) _samples.RemoveRange(0, _samples.Count - MaxSamples);
    }
}
