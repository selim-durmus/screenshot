using System.IO;
using System.Text.Json;
using ScreenshotOCR.Models;

namespace ScreenshotOCR.Services;

public class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenshotOCR");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? SettingsChanged;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) Current = loaded;
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(SettingsPath, json);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
