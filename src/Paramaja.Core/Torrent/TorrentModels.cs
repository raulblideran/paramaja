using System.Text.Json.Serialization;

namespace Paramaja.Core.Torrent;

public sealed record TorrentFile(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("len")] long Length,
    [property: JsonPropertyName("is_video")] bool IsVideo)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record TorrentInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("info_hash")] string InfoHash,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("files")] IReadOnlyList<TorrentFile> Files,
    [property: JsonPropertyName("selected_file")] int? SelectedFile,
    [property: JsonPropertyName("stream_url")] string? StreamUrl);

public sealed record TorrentStats(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("progress_bytes")] long ProgressBytes,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("file_progress")] IReadOnlyList<long> FileProgress,
    [property: JsonPropertyName("download_mbps")] double DownloadMiBps,
    [property: JsonPropertyName("upload_mbps")] double UploadMiBps,
    [property: JsonPropertyName("peers_live")] int PeersLive,
    [property: JsonPropertyName("peers_connecting")] int PeersConnecting,
    [property: JsonPropertyName("peers_seen")] int PeersSeen);

public sealed record MegaInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("stream_url")] string StreamUrl);

public sealed record TorrentEngineInfo(
    [property: JsonPropertyName("http_addr")] string HttpAddress,
    [property: JsonPropertyName("listen_addr")] string? ListenAddress);

public sealed class TorrentEngineConfig
{
    [JsonPropertyName("cache_dir")] public required string CacheDir { get; init; }
    [JsonPropertyName("http_port")] public int HttpPort { get; init; }
    [JsonPropertyName("listen_port")] public int ListenPort { get; init; }
    [JsonPropertyName("enable_dht")] public bool EnableDht { get; init; } = true;
    [JsonPropertyName("enable_trackers")] public bool EnableTrackers { get; init; } = true;
    [JsonPropertyName("enable_upnp")] public bool EnableUpnp { get; init; } = true;
    /// <summary>How far ahead of the playhead torrent pieces are fetched, in order.</summary>
    [JsonPropertyName("stream_readahead_mb")] public int StreamReadaheadMb { get; init; } = 256;
}

public sealed class TorrentException(string message) : Exception(message);
