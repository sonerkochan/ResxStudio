using System.Collections.Generic;
using ResxStudio.Services;

namespace ResxStudio.ViewModels;

/// <summary>What the autofill window needs: target languages, which keys, and saved settings.</summary>
public sealed record AutofillContext(
    IReadOnlyList<LanguageViewModel> Languages,
    IReadOnlyList<EntryViewModel> AllEntries,
    IReadOnlyList<EntryViewModel> ViewEntries,
    EntryViewModel? SelectedEntry,
    AppSettings Settings);
