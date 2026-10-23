using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResxStudio.Services;

/// <summary>
/// Remembers which values were machine-translated and not reviewed yet, across sessions.
/// Stored in %AppData%\ResxStudio\review, never in your .resx files. A flag is only restored
/// if the text is still exactly what the machine produced, so edits made elsewhere clear it.
/// </summary>
public static class ReviewStore
{
    // languageId -> key -> machine-translated text
    public static Dictionary<string, Dictionary<string, string>> Load(string neutralPath)
    {
        try
        {
            var path = PathFor(neutralPath);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception)
        {
            // Losing review marks is not worth an error message.
        }
        return new();
    }

    public static void Save(string neutralPath, Dictionary<string, Dictionary<string, string>> data)
    {
        try
        {
            var path = PathFor(neutralPath);
            if (data.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(data));
        }
        catch (Exception)
        {
            // See above.
        }
    }

    private static string PathFor(string neutralPath)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(neutralPath).ToLowerInvariant());
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResxStudio", "review", hash + ".json");
    }
}
