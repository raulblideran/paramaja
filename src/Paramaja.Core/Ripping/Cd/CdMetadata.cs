using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>A track's tags.</summary>
public sealed record TrackTags(int Number, string Title, string Artist, string? Isrc = null, string? RecordingId = null, string? TrackId = null);

/// <summary>
/// The album's tags, as a rip is tagged: from MusicBrainz, the disc's CD-Text, or typed in.
/// </summary>
public sealed record AlbumTags
{
    public string Album { get; init; } = "Unknown Album";
    public string AlbumArtist { get; init; } = "Unknown Artist";
    public string? Date { get; init; }
    public string? Genre { get; init; }
    public string? Label { get; init; }
    public string? CatalogNumber { get; init; }
    public string? Barcode { get; init; }
    public string? Country { get; init; }
    public int DiscNumber { get; init; } = 1;
    public int DiscTotal { get; init; } = 1;
    public string? ReleaseId { get; init; }
    public string? ReleaseGroupId { get; init; }
    public string? AlbumArtistId { get; init; }
    public string? DiscId { get; init; }
    public IReadOnlyList<TrackTags> Tracks { get; init; } = [];
    /// <summary>Where the tags came from: "MusicBrainz", "CD-Text" or nothing (typed in).</summary>
    public string? Source { get; init; }

    public string? Year => Date is { Length: >= 4 } d ? d[..4] : null;

    /// <summary>How a release reads in a list: "Dangerous — 1991-11-26 US · Epic EK 45400".</summary>
    public string Description =>
        string.Join(" · ", new[] { $"{AlbumArtist} — {Album}", string.Join(" ", new[] { Date, Country }.Where(s => !string.IsNullOrEmpty(s))),
                string.Join(" ", new[] { Label, CatalogNumber }.Where(s => !string.IsNullOrEmpty(s))),
                DiscTotal > 1 ? $"disc {DiscNumber}/{DiscTotal}" : null }
            .Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Tags for a disc nothing knows: "Track 01" and so on, or the disc's CD-Text.</summary>
    public static AlbumTags Placeholder(CdToc toc, CdText? text) => new()
    {
        Album = text?.Title is { Length: > 0 } title ? title : "Unknown Album",
        AlbumArtist = text?.Performer is { Length: > 0 } performer ? performer : "Unknown Artist",
        Tracks = toc.AudioTracks.Select(t => new TrackTags(t.Number,
            text?.TrackTitles.GetValueOrDefault(t.Number) ?? $"Track {t.Number:00}",
            text?.TrackPerformers.GetValueOrDefault(t.Number) ?? text?.Performer ?? "Unknown Artist")).ToList(),
        Barcode = text?.Upc,
        Source = text is null ? null : "CD-Text",
    };
}

/// <summary>
/// Looks a disc up on MusicBrainz by its disc ID (or, failing that, by its TOC), with the cover
/// from the Cover Art Archive. MusicBrainz asks for a named User-Agent and one request a second.
/// </summary>
public static class CdMetadata
{
    public const string MusicBrainz = "https://musicbrainz.org/ws/2/";
    private static readonly SemaphoreSlim Pace = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    public static async Task<IReadOnlyList<AlbumTags>> LookupAsync(HttpClient http, CdToc toc, CancellationToken ct = default)
    {
        const string inc = "inc=recordings+artist-credits+labels+release-groups+isrcs+genres&fmt=json";
        var json = await GetAsync(http, $"{MusicBrainz}discid/{toc.MusicBrainzDiscId}?{inc}", ct).ConfigureAwait(false)
                   ?? await GetAsync(http, $"{MusicBrainz}discid/-?toc={toc.MusicBrainzToc}&cdstubs=no&{inc}", ct).ConfigureAwait(false);
        return json is null ? [] : Parse(json, toc);
    }

    private static async Task<string?> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        await Pace.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastRequest + TimeSpan.FromSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Pace.Release();
        }
    }

    /// <summary>The releases in a discid lookup's answer, each with the medium (disc) that is this one.</summary>
    public static IReadOnlyList<AlbumTags> Parse(string json, CdToc toc)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("releases", out var releases)) return [];
        var discId = toc.MusicBrainzDiscId;
        var list = new List<AlbumTags>();
        foreach (var release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("media", out var media)) continue;
            var all = media.EnumerateArray().ToList();
            // The medium with this disc ID, else (a fuzzy TOC match) the first with as many tracks.
            var medium = all.FirstOrDefault(m => m.TryGetProperty("discs", out var discs) && discs.EnumerateArray().Any(d => Str(d, "id") == discId));
            if (medium.ValueKind == JsonValueKind.Undefined)
                medium = all.FirstOrDefault(m => m.TryGetProperty("tracks", out var t) && t.GetArrayLength() == toc.AudioTrackCount);
            if (medium.ValueKind == JsonValueKind.Undefined || !medium.TryGetProperty("tracks", out var trackList)) continue;
            var albumArtist = Credit(release);
            var audio = toc.AudioTracks.ToList();
            var tracks = trackList.EnumerateArray().Select((t, i) =>
            {
                var recording = t.TryGetProperty("recording", out var r) ? r : default;
                var isrc = recording.ValueKind == JsonValueKind.Object && recording.TryGetProperty("isrcs", out var isrcs) && isrcs.GetArrayLength() > 0
                    ? isrcs[0].GetString() : null;
                var number = i < audio.Count ? audio[i].Number : i + 1;
                return new TrackTags(number, Str(t, "title") ?? $"Track {number:00}", Credit(t) ?? albumArtist ?? "Unknown Artist", isrc,
                    recording.ValueKind == JsonValueKind.Object ? Str(recording, "id") : null, Str(t, "id"));
            }).ToList();
            var label = release.TryGetProperty("label-info", out var labels) && labels.GetArrayLength() > 0 ? labels[0] : default;
            var group = release.TryGetProperty("release-group", out var g) ? g : default;
            list.Add(new AlbumTags
            {
                Album = Str(release, "title") ?? "Unknown Album",
                AlbumArtist = albumArtist ?? "Unknown Artist",
                AlbumArtistId = release.TryGetProperty("artist-credit", out var credit) && credit.GetArrayLength() > 0
                                && credit[0].TryGetProperty("artist", out var artist) ? Str(artist, "id") : null,
                Date = Str(release, "date"),
                Country = Str(release, "country"),
                Barcode = Str(release, "barcode") is { Length: > 0 } barcode ? barcode : null,
                Label = label.ValueKind == JsonValueKind.Object && label.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Object ? Str(l, "name") : null,
                CatalogNumber = label.ValueKind == JsonValueKind.Object ? Str(label, "catalog-number") : null,
                Genre = Genre(release) ?? (group.ValueKind == JsonValueKind.Object ? Genre(group) : null),
                DiscNumber = medium.TryGetProperty("position", out var position) && position.TryGetInt32(out var p) ? p : 1,
                DiscTotal = Math.Max(1, all.Count),
                ReleaseId = Str(release, "id"),
                ReleaseGroupId = group.ValueKind == JsonValueKind.Object ? Str(group, "id") : null,
                DiscId = discId,
                Tracks = tracks,
                Source = "MusicBrainz",
            });
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>An artist credit as it's written: "Michael Jackson", "Michael Jackson feat. Akon".</summary>
    private static string? Credit(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var credit) || credit.GetArrayLength() == 0) return null;
        return string.Concat(credit.EnumerateArray().Select(c => (Str(c, "name") ?? "") + (Str(c, "joinphrase") ?? "")));
    }

    /// <summary>The most voted genre, capitalised as tags usually are ("Pop", "Contemporary R&amp;B").</summary>
    private static string? Genre(JsonElement e)
    {
        if (!e.TryGetProperty("genres", out var genres) || genres.GetArrayLength() == 0) return null;
        var top = genres.EnumerateArray().OrderByDescending(x => x.TryGetProperty("count", out var c) ? c.GetInt32() : 0).First();
        var name = Str(top, "name");
        return name is null ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name).Replace("R&B", "R&B").Replace("R&b", "R&B");
    }

    /// <summary>
    /// The release's front cover from the Cover Art Archive: 1200 px, else 500 px, else the
    /// original, else the release group's (another edition's). The archive's image store has its
    /// bad moments (503s), so each size gets a second try. Null where there's none (or only junk).
    /// </summary>
    public static async Task<byte[]?> CoverAsync(HttpClient http, AlbumTags album, CancellationToken ct = default)
    {
        var urls = new List<string>();
        if (album.ReleaseId is { } release)
            urls.AddRange(new[] { "front-1200", "front-500", "front" }.Select(size => $"https://coverartarchive.org/release/{release}/{size}"));
        if (album.ReleaseGroupId is { } group)
            urls.AddRange(new[] { "front-1200", "front" }.Select(size => $"https://coverartarchive.org/release-group/{group}/{size}"));
        foreach (var url in urls)
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound) break; // none at this size: the next
                    if (!response.IsSuccessStatusCode) continue;
                    var image = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (IsJpeg(image)) return image;
                    break;
                }
                catch (HttpRequestException) { }
            }
        return null;
    }

    /// <summary>A JPEG (the archive's thumbnails always are; an original may be a PNG, which is skipped for one of them).</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> image) => image.Length > 1000 && image[0] == 0xFF && image[1] == 0xD8;
}
