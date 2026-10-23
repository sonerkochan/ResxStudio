using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ResxStudio.Services;

public enum TokenKind { Url, FormatItem, NamedPlaceholder, Html, Entity, Razor, Printf, Escape, ProtectedTerm }

public readonly record struct Token(int Index, int Length, string Text, TokenKind Kind);

/// <summary>
/// Finds text that must survive translation unchanged (placeholders, markup, code, names),
/// replaces it with numbered markers before translating, and puts it back afterwards.
/// </summary>
public static class TokenProtector
{
    private static readonly Regex TokenRegex = new(
        @"(?<url>https?://[^\s<>""']+)" +
        @"|(?<fmt>\{\d+(?:\s*,\s*-?\d+)?(?::[^{}]*)?\})" +                         // {0}  {1,-10}  {2:N2}
        @"|(?<named>\{\{[^{}]+\}\}|\$\{[^{}]+\}|\{[A-Za-z_][\w.]*(?::[^{}]*)?\})" + // {{name}}  ${name}  {name}
        @"|(?<html></?[A-Za-z][\w:-]*(?:\s[^<>]*)?/?>)" +                           // <b>  </a>  <br/>
        @"|(?<entity>&(?:[A-Za-z][A-Za-z0-9]*|#\d+|#[xX][0-9A-Fa-f]+);)" +          // &nbsp;  &#169;
        @"|(?<razor>(?<![\w@.])@(?:\([^()]*\)|[A-Za-z_][\w.]*))" +                   // @Model.Name  @(x + 1), not emails
        @"|(?<printf>%(?:\d+\$)?[-+#0]*\d*(?:\.\d+)?[sdifxXeEgGcuo@])" +            // %s  %1$d  %.2f
        @"|(?<escape>\\[nrt""\\])",                                                // \n written as two characters
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Markers look like {0}. Some engines add spaces or switch to full-width braces, so accept those too.
    private static readonly Regex MarkerRegex = new(@"[{｛]\s*(\d+)\s*[}｝]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HtmlTagName = new(@"(?<=^</?)[A-Za-z][\w:-]*", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static List<Token> FindTokens(string text, IReadOnlyList<string> protectedTerms)
    {
        var tokens = new List<Token>();
        foreach (Match match in TokenRegex.Matches(text))
            tokens.Add(new Token(match.Index, match.Length, match.Value, KindOf(match)));

        var terms = protectedTerms
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length);

        foreach (var term in terms)
        {
            var start = 0;
            while (start < text.Length)
            {
                var index = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var end = index + term.Length;
                var wholeWord = (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
                                && (end == text.Length || !char.IsLetterOrDigit(text[end]));
                var overlaps = tokens.Any(t => index < t.Index + t.Length && t.Index < end);
                if (wholeWord && !overlaps)
                    tokens.Add(new Token(index, term.Length, text.Substring(index, term.Length), TokenKind.ProtectedTerm));
                start = end;
            }
        }

        tokens.Sort((a, b) => a.Index.CompareTo(b.Index));
        return tokens;
    }

    /// <summary>Replaces each token with a marker: the i-th token becomes {i}.</summary>
    public static string Mask(string text, IReadOnlyList<Token> tokens)
    {
        var builder = new StringBuilder();
        var position = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            builder.Append(text, position, tokens[i].Index - position);
            builder.Append('{').Append(i).Append('}');
            position = tokens[i].Index + tokens[i].Length;
        }
        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    /// <summary>Puts the tokens back. Fails if a marker was lost, duplicated or invented.</summary>
    public static bool TryRestore(string translated, IReadOnlyList<Token> tokens, out string restored)
    {
        var counts = new int[tokens.Count];
        var valid = true;
        restored = MarkerRegex.Replace(translated, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out var index) || index >= tokens.Count)
            {
                valid = false;
                return match.Value;
            }
            counts[index]++;
            return tokens[index].Text;
        });
        return valid && counts.All(c => c == 1);
    }

    /// <summary>True when there are words left to translate once the markers are removed.</summary>
    public static bool HasTranslatableText(string maskedText)
        => MarkerRegex.Replace(maskedText, "").Any(char.IsLetter);

    /// <summary>
    /// Placeholders and markup used to compare a translation with the default text.
    /// URLs are left out (they're often localized); HTML tags are compared by name only,
    /// because attributes such as title or alt may be translated.
    /// </summary>
    public static List<string> ExtractPlaceholders(string text)
    {
        var result = new List<string>();
        foreach (Match match in TokenRegex.Matches(text))
        {
            if (match.Groups["url"].Success) continue;
            if (match.Groups["html"].Success)
            {
                var closing = match.Value.StartsWith("</", StringComparison.Ordinal);
                var name = HtmlTagName.Match(match.Value).Value.ToLowerInvariant();
                result.Add("<" + (closing ? "/" : "") + name + ">");
                continue;
            }
            result.Add(Whitespace.Replace(match.Value, ""));
        }
        return result;
    }

    public static bool IsEscape(string placeholder) => placeholder.StartsWith('\\');

    private static TokenKind KindOf(Match match)
    {
        if (match.Groups["url"].Success) return TokenKind.Url;
        if (match.Groups["fmt"].Success) return TokenKind.FormatItem;
        if (match.Groups["named"].Success) return TokenKind.NamedPlaceholder;
        if (match.Groups["html"].Success) return TokenKind.Html;
        if (match.Groups["entity"].Success) return TokenKind.Entity;
        if (match.Groups["razor"].Success) return TokenKind.Razor;
        if (match.Groups["printf"].Success) return TokenKind.Printf;
        return TokenKind.Escape;
    }
}
