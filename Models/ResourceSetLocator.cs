using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ResxStudio.Models;

public sealed record LanguageFile(string Code, string FilePath);

public sealed record ResourceSetInfo(
    string Folder,
    string BaseName,
    char Separator,
    string NeutralPath,
    IReadOnlyList<LanguageFile> Languages);

/// <summary>
/// Given any .resx in a set, finds the default file and every language file next to it.
/// Supports both "Translations.bg.resx" (standard .NET) and "Translations_bg.resx".
/// </summary>
public static class ResourceSetLocator
{
    private static readonly Regex CultureSuffix = new(
        @"^(?<base>.+)[._](?<code>[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)$",
        RegexOptions.CultureInvariant);

    public static ResourceSetInfo Locate(string anyFile)
    {
        var fullPath = Path.GetFullPath(anyFile);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new IOException("Couldn't work out the folder of that file.");
        var stem = Path.GetFileNameWithoutExtension(fullPath);

        // If a language file was picked, step back to its default file.
        var baseName = stem;
        var match = CultureSuffix.Match(stem);
        if (match.Success
            && IsCulture(match.Groups["code"].Value)
            && File.Exists(Path.Combine(folder, match.Groups["base"].Value + ".resx")))
        {
            baseName = match.Groups["base"].Value;
        }

        var neutralPath = Path.Combine(folder, baseName + ".resx");
        if (!File.Exists(neutralPath))
            throw new FileNotFoundException(
                $"Couldn't find the default file {baseName}.resx in {folder}. Open the file that has no language code in its name.");

        var languages = new List<LanguageFile>();
        char? separator = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.resx"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length <= baseName.Length + 1) continue;
            if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;

            var sep = name[baseName.Length];
            if (sep != '.' && sep != '_') continue;

            var code = name[(baseName.Length + 1)..];
            if (!IsCulture(code)) continue;

            languages.Add(new LanguageFile(code, file));
            separator ??= sep;
        }

        languages.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));
        return new ResourceSetInfo(folder, baseName, separator ?? '.', neutralPath, languages);
    }

    public static bool IsCulture(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            return CultureInfo.GetCultureInfo(code, predefinedOnly: true).Name.Length > 0;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
