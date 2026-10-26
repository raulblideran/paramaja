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

    /// <summary>Writes atomically so a crash mid-save never leaves a truncated file.</summary>
    public void Save(PlayerSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
