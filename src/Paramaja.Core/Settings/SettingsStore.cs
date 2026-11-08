using System.Text.Json;

namespace Paramaja.Core.Settings;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string FilePath { get; } = path;

    /// <summary>Loads settings; a missing file gives defaults, a corrupt one is backed up and replaced.</summary>
    public PlayerSettings Load()
    {
        PlayerSettings? settings = null;
        if (File.Exists(FilePath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(FilePath), Json);
            }
            catch (JsonException)
            {
                try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); } catch (IOException) { }
            }
        }
        settings ??= new PlayerSettings();
        settings.Normalize();
        return settings;
    }

    /// <summary>The file name an export gets by default.</summary>
    public const string ExportName = "paramaja-settings.json";

    /// <summary>
    /// The settings in a file someone hands over: an exported one, or another install's own
    /// settings.json. Null if it isn't one (or can't be read). Unlike <see cref="Load"/> it
    /// writes nothing next to the file: it may be on a read-only disk, or someone else's.
    /// </summary>
    public static PlayerSettings? ReadFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFile) return null;
            var text = File.ReadAllText(path);
            if (!LooksLikeSettings(text)) return null;
            var settings = JsonSerializer.Deserialize<PlayerSettings>(text, Json);
            settings?.Normalize();
            return settings;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private const long MaxFile = 1 << 20;

    /// <summary>A .json file small enough, with Paramaja's settings in it (a "settingsVersion" at the top).</summary>
    public static bool IsSettingsFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaxFile && LooksLikeSettings(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool LooksLikeSettings(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("settingsVersion", out _);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The settings as a file to carry to another install (the same JSON as settings.json).</summary>
    public static void Export(PlayerSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
    }

    /// <summary>Writes atomically so a crash mid-save never leaves a truncated file.</summary>
    public void Save(PlayerSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
