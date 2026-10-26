using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paramaja.Core.Torrent;

public interface ITorrentService : IDisposable
{
    bool IsStarted { get; }
    TorrentEngineInfo Start(TorrentEngineConfig config);
    Task<TorrentInfo> AddAsync(string magnet, TimeSpan timeout, CancellationToken ct = default);
    /// <summary>Resolves a MEGA file link to a local URL that streams it decrypted.</summary>
    Task<MegaInfo> AddMegaAsync(string link, TimeSpan timeout, CancellationToken ct = default);
    string SelectFile(int torrentId, int fileIndex);
    TorrentStats GetStats(int torrentId);
    void Remove(int torrentId, bool deleteFiles);
}

/// <summary>Managed facade over the Rust <c>paramaja_torrent</c> library.</summary>
public sealed class TorrentService : ITorrentService
{
    public bool IsStarted { get; private set; }

    internal static T Unwrap<T>(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new TorrentException("invalid response from torrent engine");
        if (node["ok"]?.GetValue<bool>() != true)
            throw new TorrentException(node["error"]?.GetValue<string>() ?? "unknown torrent error");
        var data = node["data"];
        if (typeof(T) == typeof(Unit)) return default!;
        return (data is null ? default : data.Deserialize<T>())
               ?? throw new TorrentException("torrent engine returned no data");
    }

    internal readonly struct Unit;

    public TorrentEngineInfo Start(TorrentEngineConfig config)
    {
        Directory.CreateDirectory(config.CacheDir);
        var info = Unwrap<TorrentEngineInfo>(TorrentNative.Take(TorrentNative.nt_init(JsonSerializer.Serialize(config))));
        IsStarted = true;
        return info;
    }

    public Task<TorrentInfo> AddAsync(string magnet, TimeSpan timeout, CancellationToken ct = default)
    {
        EnsureStarted();
        // Metadata resolution blocks inside Rust; keep it off the UI thread.
        return Task.Run(() => Unwrap<TorrentInfo>(
            TorrentNative.Take(TorrentNative.nt_add(magnet, (ulong)timeout.TotalMilliseconds))), ct);
    }

    public Task<MegaInfo> AddMegaAsync(string link, TimeSpan timeout, CancellationToken ct = default)
    {
        EnsureStarted();
        return Task.Run(() => Unwrap<MegaInfo>(
            TorrentNative.Take(TorrentNative.nt_mega_add(link, (ulong)timeout.TotalMilliseconds))), ct);
    }

    public string SelectFile(int torrentId, int fileIndex)
    {
        EnsureStarted();
        return Unwrap<string>(TorrentNative.Take(TorrentNative.nt_select((nuint)torrentId, (nuint)fileIndex)));
    }

    public TorrentStats GetStats(int torrentId)
    {
        EnsureStarted();
        return Unwrap<TorrentStats>(TorrentNative.Take(TorrentNative.nt_stats((nuint)torrentId)));
    }

    public void Remove(int torrentId, bool deleteFiles)
    {
        EnsureStarted();
        Unwrap<Unit>(TorrentNative.Take(TorrentNative.nt_remove((nuint)torrentId, deleteFiles)));
    }

    private void EnsureStarted()
    {
        if (!IsStarted) throw new TorrentException("torrent engine not started");
    }

    public void Dispose()
    {
        if (!IsStarted) return;
        IsStarted = false;
        TorrentNative.Take(TorrentNative.nt_shutdown());
    }
}
