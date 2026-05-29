using System.Text.Json;

namespace SlinnerBMusicStudio;

/// <summary>
/// Small set of preferences persisted between runs. Stored as JSON under
/// %AppData% so the .exe itself stays portable (the exe never writes beside
/// itself). Loading and saving are best-effort and never throw.
/// </summary>
public class Settings
{
    public string? LastMicName { get; set; }
    public string? LastSpeakerName { get; set; }
    public string? LastFolder { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlinnerBMusicStudio",
        "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch
        {
            // Settings file corrupt or unreadable; fall through to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Persisting settings is best-effort; never crash the app on save failure.
        }
    }
}
