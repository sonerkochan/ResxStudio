using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ResxStudio.Services;

public class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}

/// <summary>The daily free allowance is used up. Nothing to do but try again tomorrow.</summary>
public sealed class TranslationQuotaException : TranslationException
{
    public TranslationQuotaException(string message) : base(message) { }
}

/// <summary>
/// Client for the free MyMemory API (https://mymemory.translated.net/doc/spec.php).
/// Free use: 5,000 characters a day, or 50,000 with an email address in the "de" parameter.
/// One request may contain at most 500 bytes of text.
/// </summary>
public sealed class MyMemoryClient
{
    /// <summary>Stay under the 500-byte limit with some room to spare.</summary>
    public const int MaxQueryBytes = 450;

    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string? _email;

    public MyMemoryClient(string? email)
    {
        _email = !string.IsNullOrWhiteSpace(email) && email.Contains('@') ? email.Trim() : null;
    }

    public bool HasEmail => _email != null;
    public int CharactersSent { get; private set; }

    /// <summary>MyMemory uses region codes for Chinese.</summary>
    public static string ToServiceCode(string culture) => culture.ToLowerInvariant() switch
    {
        "zh-hans" or "zh-cn" or "zh-sg" => "zh-CN",
        "zh-hant" or "zh-tw" or "zh-hk" or "zh-mo" => "zh-TW",
        _ => culture,
    };

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        var url = new StringBuilder("https://api.mymemory.translated.net/get?q=")
            .Append(Uri.EscapeDataString(text))
            .Append("&langpair=").Append(Uri.EscapeDataString(sourceLanguage + "|" + targetLanguage));
        if (_email != null) url.Append("&de=").Append(Uri.EscapeDataString(_email));

        CharactersSent += text.Length;
        using var response = await Http.GetAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode == 429)
            throw new TranslationQuotaException("MyMemory's daily limit is used up.");
        if (!response.IsSuccessStatusCode)
            throw new TranslationException($"MyMemory answered with HTTP {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var status = ReadStatus(root);

        var translated = root.TryGetProperty("responseData", out var data)
                         && data.ValueKind == JsonValueKind.Object
                         && data.TryGetProperty("translatedText", out var value)
                         && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

        var quotaFinished = root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True;
        if (quotaFinished || status == 429 || translated.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            throw new TranslationQuotaException(translated.Length > 0 ? translated : "MyMemory's daily limit is used up.");

        if (status != 200)
        {
            var details = root.TryGetProperty("responseDetails", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            throw new TranslationException(!string.IsNullOrWhiteSpace(details)
                ? details!
                : translated.Length > 0 ? translated : $"MyMemory answered with status {status}.");
        }

        // A short pause between calls keeps us well within the service's rate limits.
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        // The service sometimes returns HTML-encoded characters such as &#39;.
        return WebUtility.HtmlDecode(translated);
    }

    private static int ReadStatus(JsonElement root)
    {
        if (!root.TryGetProperty("responseStatus", out var status)) return 200;
        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var number)) return number;
        if (status.ValueKind == JsonValueKind.String && int.TryParse(status.GetString(), out var parsed)) return parsed;
        return 0;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ResxStudio/1.1");
        return client;
    }
}
