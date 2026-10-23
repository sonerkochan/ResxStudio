using System;
using System.Collections.Generic;
using System.Linq;

namespace ResxStudio.Models;

/// <summary>
/// Key-level three-way merge, used when a file changed on disk while you were editing it.
/// "Baseline" is the file as ResxStudio last loaded or saved it, "theirs" is what's on disk now,
/// "mine" is what you have in the editor.
/// </summary>
public static class ResxMerger
{
    public static List<KeyValuePair<string, string>> Merge(
        IReadOnlyList<KeyValuePair<string, string>> mine,
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyList<KeyValuePair<string, string>> theirs,
        IReadOnlyDictionary<string, string> renames,
        List<string> conflicts)
    {
        // Bring everything into the new key names so renames line up.
        string Renamed(string key) => renames.TryGetValue(key, out var newKey) ? newKey : key;

        var baseValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in baseline) baseValues[Renamed(pair.Key)] = pair.Value;

        var theirValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in theirs) theirValues[Renamed(pair.Key)] = pair.Value;

        var myValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in mine) myValues[pair.Key] = pair.Value;

        var order = mine.Select(p => p.Key).Concat(theirs.Select(p => Renamed(p.Key)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<KeyValuePair<string, string>>();

        foreach (var key in order)
        {
            if (!seen.Add(key)) continue;

            myValues.TryGetValue(key, out var my);
            baseValues.TryGetValue(key, out var original);
            theirValues.TryGetValue(key, out var their);

            string? chosen;
            if (my == original) chosen = their;                       // only they changed it (or nobody did)
            else if (their == original || their == my) chosen = my;  // only I changed it, or we agree
            else
            {
                chosen = my;                                          // both changed it differently: mine wins
                conflicts.Add(key);
            }

            if (chosen != null) result.Add(new KeyValuePair<string, string>(key, chosen));
        }

        return result;
    }
}
