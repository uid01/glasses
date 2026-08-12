using System;
using System.IO;
using System.Text.Json;
using PcHostGui.Models;

namespace PcHostGui.Services;

/// <summary>Loads/saves <see cref="GuiSettings"/> as JSON under the user's AppData folder.</summary>
public static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XrealBridge", "gui_settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<GuiSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable settings file -- fall through to defaults rather than crash the
            // app on startup over a settings file, which the user can always reconfigure.
        }

        return new GuiSettings();
    }

    public static void Save(GuiSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
