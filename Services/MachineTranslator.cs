using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ResxStudio.Services;

/// <summary>
/// Translates one resource value safely: keeps line breaks and surrounding spaces,
/// protects placeholders and names, and splits long text to fit the API limit.
/// </summary>
public sealed class MachineTranslator
{
    private static readonly Regex SentenceBreak = new(@"(?<=[.!?…])\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly MyMemoryClient _client;
    private readonly IReadOnlyList<string> _protectedTerms;

    public MachineTranslator(MyMemoryClient client, IReadOnlyList<string> protectedTerms)
    {
        _client = client;
        _protectedTerms = protectedTerms;
    }

    public async Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct)
    {
        // Engines tend to drop or move line breaks, so each line is translated on its own.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = await TranslateLineAsync(lines[i], source, target, ct);
        return string.Join("\n", lines);
    }

    private async Task<string> TranslateLineAsync(string line, string source, string target, CancellationToken ct)
    {
        var (prefix, core, suffix) = SplitEdges(line);
        if (!core.Any(char.IsLetter)) return line;

        var tokens = TokenProtector.FindTokens(core, _protectedTerms);
        var masked = TokenProtector.Mask(core, tokens);

        // Nothing but placeholders and names (e.g. "My app"): keep as-is, no API call needed.
        if (!TokenProtector.HasTranslatableText(masked)) return line;

        var translated = await TranslateLongAsync(masked, source, target, ct);
        if (TokenProtector.TryRestore(translated, tokens, out var restored))
            return prefix + restored + suffix;

        // The service damaged a marker. Translate only the text between the tokens instead,
        // so every token is copied over untouched. Word order may be less natural, and the
        // Problems list will still let you review it.
        var builder = new StringBuilder(prefix);
        var position = 0;
        foreach (var token in tokens)
        {
            builder.Append(await TranslatePlainAsync(core[position..token.Index], source, target, ct));
            builder.Append(token.Text);
            position = token.Index + token.Length;
        }
        builder.Append(await TranslatePlainAsync(core[position..], source, target, ct));
        return builder.Append(suffix).ToString();
    }

    private async Task<string> TranslatePlainAsync(string text, string source, string target, CancellationToken ct)
    {
        var (prefix, core, suffix) = SplitEdges(text);
        if (!core.Any(char.IsLetter)) return text;
        return prefix + await TranslateLongAsync(core, source, target, ct) + suffix;
    }

    private async Task<string> TranslateLongAsync(string text, string source, string target, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var chunk in SplitIntoChunks(text, MyMemoryClient.MaxQueryBytes))
            parts.Add(await _client.TranslateAsync(chunk, source, target, ct));
        return string.Join(" ", parts);
    }

    /// <summary>Splits on sentence ends (then on spaces if needed) so each chunk fits in maxBytes of UTF-8.</summary>
    internal static List<string> SplitIntoChunks(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return new List<string> { text };

        var pieces = new List<string>();
        foreach (var sentence in SentenceBreak.Split(text))
            pieces.AddRange(SplitOnSpaces(sentence, maxBytes));
        return Pack(pieces, maxBytes);
    }

    private static List<string> SplitOnSpaces(string sentence, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(sentence) <= maxBytes) return new List<string> { sentence };
        return Pack(sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries), maxBytes);
    }

    private static List<string> Pack(IEnumerable<string> pieces, int maxBytes)
    {
        var result = new List<string>();
        var current = "";
        foreach (var piece in pieces)
        {
            var candidate = current.Length == 0 ? piece : current + " " + piece;
            if (Encoding.UTF8.GetByteCount(candidate) <= maxBytes)
            {
                current = candidate;
                continue;
            }
            if (current.Length > 0) result.Add(current);
            current = piece;
        }
        if (current.Length > 0) result.Add(current);
        return result;
    }

    private static (string Prefix, string Core, string Suffix) SplitEdges(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (text[..start], text[start..end], text[end..]);
    }
}
