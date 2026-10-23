using System;
using System.Collections.Generic;
using System.ComponentModel;

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

    /// <summary>Which languages hold machine-translated text that nobody has reviewed yet.</summary>
    public MachineTranslatedFlags MachineTranslated { get; } = new();

    /// <summary>Text for a language id; null when the key doesn't exist in that file.</summary>
    public string? this[string languageId]
    {
        get => _values.TryGetValue(languageId, out var value) ? value : null;
        set
        {
            var current = this[languageId];
            if (string.Equals(current ?? "", value ?? "", StringComparison.Ordinal)) return;
            _values[languageId] = value;
            // Any edit by a person counts as a review.
            MachineTranslated.Set(languageId, false);
            OnPropertyChanged("Item[]");
            OnPropertyChanged(nameof(IsOrphan));
            _valueChanged(this, languageId);
        }
    }

    public void SetMachineTranslation(string languageId, string value)
    {
        this[languageId] = value;
        MachineTranslated.Set(languageId, true);
    }

    public bool HasValue(string languageId) => !string.IsNullOrEmpty(this[languageId]);

    /// <summary>Exists in a translation but not in the default file, so there is no fallback.</summary>
    public bool IsOrphan => this[LanguageViewModel.NeutralId] == null;

    /// <summary>Sets a value while loading, without marking anything as changed.</summary>
    internal void Load(string languageId, string value) => _values[languageId] = value;
}

/// <summary>Bindable set of language ids, so the grid can bind to MachineTranslated[bg].</summary>
public sealed class MachineTranslatedFlags : INotifyPropertyChanged
{
    private readonly HashSet<string> _languageIds = new(StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool this[string languageId] => _languageIds.Contains(languageId);

    public bool Any => _languageIds.Count > 0;

    internal void Set(string languageId, bool value)
    {
        var changed = value ? _languageIds.Add(languageId) : _languageIds.Remove(languageId);
        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
