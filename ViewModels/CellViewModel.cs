using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ResxStudio.Services;

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
        MarkReviewedCommand = new RelayCommand(() => Entry.MachineTranslated.Set(Language.Id, false));

        Entry.PropertyChanged += OnEntryChanged;
        Entry.MachineTranslated.PropertyChanged += OnMachineTranslatedChanged;
        UpdateProblems();
    }

    public EntryViewModel Entry { get; }
    public LanguageViewModel Language { get; }
    public RelayCommand CopyFromDefaultCommand { get; }
    public RelayCommand MarkReviewedCommand { get; }

    public string? Value
    {
        get => Entry[Language.Id];
        set => Entry[Language.Id] = value;
    }

    public bool IsMissing => !Entry.HasValue(Language.Id);
    public bool IsMachineTranslated => Entry.MachineTranslated[Language.Id];

    /// <summary>Placeholder and format problems in this box, checked as you type.</summary>
    public string ProblemText { get; private set; } = "";
    public bool HasProblem => ProblemText.Length > 0;
    public bool ProblemIsError { get; private set; }

    private void UpdateProblems()
    {
        var defaultText = Entry[LanguageViewModel.NeutralId];
        IEnumerable<TranslationProblem> problems;
        if (defaultText == null)
            problems = Enumerable.Empty<TranslationProblem>();
        else if (Language.IsNeutral)
            problems = TranslationChecks.CheckDefault(defaultText);
        else
            problems = TranslationChecks.CheckTranslation(defaultText, Value ?? "");

        var shown = problems.Where(p => p.Severity != IssueSeverity.Info).ToList();
        var text = string.Join(" ", shown.Select(p => p.Message));
        var isError = shown.Any(p => p.Severity == IssueSeverity.Error);

        if (text != ProblemText)
        {
            ProblemText = text;
            OnPropertyChanged(nameof(ProblemText));
            OnPropertyChanged(nameof(HasProblem));
        }
        if (isError != ProblemIsError)
        {
            ProblemIsError = isError;
            OnPropertyChanged(nameof(ProblemIsError));
        }
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsMissing));
        UpdateProblems();
    }

    private void OnMachineTranslatedChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(IsMachineTranslated));

    public void Dispose()
    {
        Entry.PropertyChanged -= OnEntryChanged;
        Entry.MachineTranslated.PropertyChanged -= OnMachineTranslatedChanged;
    }
}
