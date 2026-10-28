namespace Paramaja.Core.Playback;

/// <summary>The last few hundred lines an engine logged, for the log file (Create Log). Thread-safe.</summary>
internal sealed class RecentLines(int max = 300)
{
    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
            while (_lines.Count > max) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> ToList()
    {
        lock (_lines) return [.. _lines];
    }
}
