using System.Text.Json;

namespace uDrivers.Services;

public sealed class AppSettings
{
    public bool CreateRestorePoint { get; set; } = true;
    public bool CheckAppUpdatesOnStart { get; set; } = true;
    public bool UseManagedServer { get; set; } = false;
    public bool SourceWindowsUpdate { get; set; } = true;
    public bool SourceManufacturer { get; set; } = true;
    public bool SourceNvidia { get; set; } = true;
}

public static class SettingsService
{
    private static string FilePath => Path.Combine(AppInfo.DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings", ex);
        }
        return new();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
