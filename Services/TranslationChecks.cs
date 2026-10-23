using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ResxStudio.Services;

public enum IssueSeverity { Error, Warning, Info }

public readonly record struct TranslationProblem(IssueSeverity Severity, string Category, string Message);

public sealed record ValidationIssue(
    IssueSeverity Severity,
    string Category,
    string Key,
    string? LanguageId,
    string Location,
    string Message);

/// <summary>Checks one text against the default text. Used by the Problems list and the editor panel.</summary>
public static class TranslationChecks
{
    public const string Malformed = "Malformed format strings";
    public const string Placeholders = "Lost or changed placeholders";
    public const string Orphans = "Missing from the default file";
    public const string Duplicates = "Duplicate keys";
    public const string Missing = "Missing translations";
    public const string Spacing = "Different spacing";
    public const string SameAsDefault = "Same as the default text";

    private static readonly Regex NumberedPlaceholder = new(@"(?<!\{)\{\d", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FormatItemBody = new(@"^\s*\d+\s*(?:,\s*-?\d+\s*)?(?::[^{}]*)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A text is treated as a format string when it uses numbered placeholders like {0}.</summary>
    public static bool LooksLikeFormatString(string text) => NumberedPlaceholder.IsMatch(text);

    public static IEnumerable<TranslationProblem> CheckDefault(string defaultText)
    {
        if (LooksLikeFormatString(defaultText) && !IsValidFormatString(defaultText, out var error))
            yield return new TranslationProblem(IssueSeverity.Error, Malformed, error);
    }

    public static IEnumerable<TranslationProblem> CheckTranslation(string defaultText, string translation)
    {
        if (string.IsNullOrEmpty(translation)) yield break;

        if (LooksLikeFormatString(defaultText) && !IsValidFormatString(translation, out var error))
            yield return new TranslationProblem(IssueSeverity.Error, Malformed, error);

        var expected = TokenProtector.ExtractPlaceholders(defaultText);
        var actual = TokenProtector.ExtractPlaceholders(translation);
        var missing = Subtract(expected, actual);
        var extra = Subtract(actual, expected);

        if (missing.Count > 0)
        {
            // A lost {0} or tag breaks the output; a lost literal \n is usually cosmetic.
            var severity = missing.Any(p => !TokenProtector.IsEscape(p)) ? IssueSeverity.Error : IssueSeverity.Warning;
            yield return new TranslationProblem(severity, Placeholders, "Missing " + Describe(missing) + " from the default text.");
        }
        if (extra.Count > 0)
            yield return new TranslationProblem(IssueSeverity.Warning, Placeholders, "Has " + Describe(extra) + ", which the default text doesn't.");

        if (StartsWithSpace(defaultText) != StartsWithSpace(translation) || EndsWithSpace(defaultText) != EndsWithSpace(translation))
            yield return new TranslationProblem(IssueSeverity.Info, Spacing, "Starts or ends with spaces differently from the default text.");

        if (translation == defaultText && defaultText.Any(char.IsLetter))
            yield return new TranslationProblem(IssueSeverity.Info, SameAsDefault, "Identical to the default text. Fine for names; otherwise it may not be translated yet.");
    }

    /// <summary>Validates .NET composite format syntax: {0}, {1,-5}, {2:N2}, with {{ and }} as escapes.</summary>
    public static bool IsValidFormatString(string text, out string error)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
                var close = text.IndexOf('}', i + 1);
                if (close < 0)
                {
                    error = "A \"{\" is never closed. Use {{ for a literal brace.";
                    return false;
                }
                var body = text.Substring(i + 1, close - i - 1);
                if (!FormatItemBody.IsMatch(body))
                {
                    error = "\"{" + body + "}\" isn't a valid placeholder. Use numbered ones like {0}.";
                    return false;
                }
                i = close;
            }
            else if (c == '}')
            {
                if (i + 1 < text.Length && text[i + 1] == '}') { i++; continue; }
                error = "A \"}\" has no matching \"{\". Use }} for a literal brace.";
                return false;
            }
        }
        error = "";
        return true;
    }

    private static List<string> Subtract(IEnumerable<string> from, IEnumerable<string> remove)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in remove) counts[item] = counts.TryGetValue(item, out var n) ? n + 1 : 1;

        var result = new List<string>();
        foreach (var item in from)
        {
            if (counts.TryGetValue(item, out var n) && n > 0) counts[item] = n - 1;
            else result.Add(item);
        }
        return result;
    }

    private static string Describe(IEnumerable<string> placeholders)
        => string.Join(", ", placeholders.Distinct().Select(p => p == "\\n" ? "\\n (line break)" : p));

    private static bool StartsWithSpace(string text) => text.Length > 0 && char.IsWhiteSpace(text[0]);
    private static bool EndsWithSpace(string text) => text.Length > 0 && char.IsWhiteSpace(text[^1]);
}
