using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ResxStudio.Services;

/// <summary>Small JSON settings file in %AppData%\ResxStudio.</summary>
public sealed class AppSettings
{
    public string? LastFile { get; set; }
    public List<string> HiddenLanguages { get; set; } = new();

    /// <summary>Language of the default .resx file, sent to the translation service as the source.</summary>
    public string SourceLanguage { get; set; } = "en";
    public string? MyMemoryEmail { get; set; }

    /// <summary>Names that are never translated. Edit them in the autofill window.</summary>
    public List<string> ProtectedTerms { get; set; } = new() { "Example 1", "Example 2", "Example 3" };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResxStudio", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A broken settings file should never stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Settings are a convenience; ignore write failures.
        }
    }
}
