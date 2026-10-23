using System;
using System.ComponentModel;

namespace ResxStudio.ViewModels;

/// <summary>One language box in the editor panel for the selected key.</summary>
public sealed class CellViewModel : ObservableObject, IDisposable
{
    public CellViewModel(EntryViewModel entry, LanguageViewModel language)
    {
        Entry = entry;
        Language = language;
        CopyFromDefaultCommand = new RelayCommand(
            () => Value = Entry[LanguageViewModel.NeutralId],
            () => !Language.IsNeutral && !string.IsNullOrEmpty(Entry[LanguageViewModel.NeutralId]));
        Entry.PropertyChanged += OnEntryChanged;
    }

    public EntryViewModel Entry { get; }
    public LanguageViewModel Language { get; }
    public RelayCommand CopyFromDefaultCommand { get; }

    public string? Value
    {
        get => Entry[Language.Id];
        set => Entry[Language.Id] = value;
    }

    public bool IsMissing => !Entry.HasValue(Language.Id);

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsMissing));
    }

    public void Dispose() => Entry.PropertyChanged -= OnEntryChanged;
}
