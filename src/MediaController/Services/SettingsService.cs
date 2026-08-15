using System.IO;
using System.Text.Json;
using MediaController.Core;
using MediaController.Models;

namespace MediaController.Services;

/// <summary>JSON settings in %AppData%\MediaController\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MediaController");

        FilePath = Path.Combine(dir, "settings.json");
    }

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        var defaults = new AppSettings();

        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.NextHotkey = Sanitize(loaded.NextHotkey, defaults.NextHotkey);
                    loaded.PreviousHotkey = Sanitize(loaded.PreviousHotkey, defaults.PreviousHotkey);
                    loaded.PlayPauseHotkey = Sanitize(loaded.PlayPauseHotkey, defaults.PlayPauseHotkey);
                    if (string.IsNullOrWhiteSpace(loaded.PreferredPlayer))
                    {
                        loaded.PreferredPlayer = null;
                    }

                    // A v0.1 file has no popup fields; System.Text.Json leaves the property
                    // initializers in place, so those simply arrive as the v0.2 defaults.
                    // Only a hand edited value needs clamping.
                    loaded.TrackPopupDurationSeconds =
                        Math.Clamp(loaded.TrackPopupDurationSeconds, 0.5, 10.0);

                    Current = loaded;
                    return Current;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("settings.json is missing or corrupted; defaults will be used and the file rewritten.", ex);
        }

        Current = defaults;
        Save(defaults);
        return Current;
    }

    public void Save(AppSettings settings)
    {
        Current = settings;

        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write-then-rename: a crash mid-save can never leave a half written settings.json.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save settings.", ex);
        }
    }

    private static HotkeySettings Sanitize(HotkeySettings? value, HotkeySettings fallback) =>
        value is { IsValid: true } ? value : fallback;
}
