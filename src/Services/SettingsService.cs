using System.IO;
using System.Text.Json;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Loads and saves the app's JSON settings file under the app-data folder.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsDirectory;
    private readonly string _settingsPath;

    public SettingsService()
        : this(null)
    {
    }

    public SettingsService(string? settingsDirectory)
    {
        _settingsDirectory = string.IsNullOrWhiteSpace(settingsDirectory)
            ? AppDataPaths.RootDirectory
            : settingsDirectory;
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
    }

    /// <summary>
    /// Loads persisted settings or returns defaults when none are available.
    /// </summary>
    public AppSettings Load()
    {
        Directory.CreateDirectory(_settingsDirectory);

        if (!File.Exists(_settingsPath))
        {
            return new AppSettings { SettingsPath = _settingsPath };
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.SettingsPath = _settingsPath;
            return settings;
        }
        catch
        {
            return new AppSettings { SettingsPath = _settingsPath };
        }
    }

    /// <summary>
    /// Saves the supplied settings to disk.
    /// </summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_settingsDirectory);
        settings.SettingsPath = _settingsPath;
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// Returns the full path to the settings file.
    /// </summary>
    public string GetSettingsPath() => _settingsPath;
}
