using System;
using System.Collections.Generic;

namespace ResxStudio.ViewModels;

/// <summary>One key with its text in every language. Bound to the grid through the indexer.</summary>
public sealed class EntryViewModel : ObservableObject
{
    public const string NoGroup = "(no section)";

    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<EntryViewModel, string> _valueChanged;
    private string _key;

    public EntryViewModel(string key, Action<EntryViewModel, string> valueChanged)
    {
        _key = key;
        OriginalKey = key;
        _valueChanged = valueChanged;
    }

    public string Key
    {
        get => _key;
        set
        {
            if (Set(ref _key, value)) OnPropertyChanged(nameof(Group));
        }
    }

    /// <summary>The key as it is in the files on disk; null for keys added in this session.</summary>
    public string? OriginalKey { get; set; }

    /// <summary>The part before the first dot, e.g. "Home" for "Home.Title".</summary>
    public string Group
    {
        get
        {
            var dot = _key.IndexOf('.');
            return dot > 0 ? _key[..dot] : NoGroup;
        }
    }

    /// <summary>Text for a language id; null when the key doesn't exist in that file.</summary>
    public string? this[string languageId]
    {
        get => _values.TryGetValue(languageId, out var value) ? value : null;
        set
        {
            var current = this[languageId];
            if (string.Equals(current ?? "", value ?? "", StringComparison.Ordinal)) return;
            _values[languageId] = value;
            OnPropertyChanged("Item[]");
            OnPropertyChanged(nameof(IsOrphan));
            _valueChanged(this, languageId);
        }
    }

    public bool HasValue(string languageId) => !string.IsNullOrEmpty(this[languageId]);

    /// <summary>Exists in a translation but not in the default file, so there is no fallback.</summary>
    public bool IsOrphan => this[LanguageViewModel.NeutralId] == null;

    /// <summary>Sets a value while loading, without marking anything as changed.</summary>
    internal void Load(string languageId, string value) => _values[languageId] = value;
}
