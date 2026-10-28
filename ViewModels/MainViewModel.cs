using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ResxStudio.Models;
using ResxStudio.Services;

namespace ResxStudio.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _isLoading;
    private string _loadingText = "";
    private string? _startupNotice;

    private ResourceSetInfo? _set;
    private string? _openedPath;
    private string _searchText = "";
    private FilterOption _filter;
    private GroupViewModel? _selectedGroup;
    private EntryViewModel? _selectedEntry;
    private bool _rebuildingGroups;

    private List<ValidationIssue> _issues = new();
    private HashSet<string> _problemKeys = new(StringComparer.Ordinal);
    private bool _showProblems;
    private IssueViewModel? _selectedIssue;
    private ProblemCategoryViewModel? _selectedCategory;

    private string? _externalChangeMessage;
    private string? _dismissedExternalChange;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _filter = FilterOptions[0];
        EntriesView = CreateView(Entries);

        // Search waits for a short pause in typing so filtering 20k+ strings never feels laggy.
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshView(); };

        // Progress, section counts and problems are recalculated shortly after edits, not on every keystroke.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += (_, _) => { _statsTimer.Stop(); RecomputeStats(); };

        OpenCommand = new RelayCommand(OpenInteractive, () => !_isLoading);
        SaveCommand = new RelayCommand(() => Save(), () => HasChanges && !_isLoading);
        ReloadCommand = new RelayCommand(Reload, () => IsReady);
        AddKeyCommand = new RelayCommand(AddKey, () => IsReady);
        RenameKeyCommand = new RelayCommand(RenameKey, () => SelectedEntry != null && !_isLoading);
        DeleteKeyCommand = new RelayCommand(DeleteKey, () => SelectedEntry != null && !_isLoading);
        AddLanguageCommand = new RelayCommand(AddLanguage, () => IsReady);
        NextMissingCommand = new RelayCommand(GoToNextMissing, () => IsReady);
        AutofillCommand = new RelayCommand(Autofill, () => IsReady);
        HideProblemsCommand = new RelayCommand(() => ShowProblems = false);
        DismissExternalChangeCommand = new RelayCommand(DismissExternalChange);
    }

    // ---- Collections -------------------------------------------------------------------------

    public ObservableCollection<LanguageViewModel> Languages { get; } = new();
    public ObservableCollection<EntryViewModel> Entries { get; private set; } = new();
    public ListCollectionView EntriesView { get; private set; }
    public ObservableCollection<GroupViewModel> Groups { get; } = new();
    public ObservableCollection<CellViewModel> SelectedCells { get; } = new();
    public ObservableCollection<ProblemCategoryViewModel> ProblemCategories { get; } = new();

    public IReadOnlyList<FilterOption> FilterOptions { get; } = new[]
    {
        new FilterOption(EntryFilter.All, "All keys"),
        new FilterOption(EntryFilter.Untranslated, "Untranslated"),
        new FilterOption(EntryFilter.AutoTranslated, "Auto-translated, to review"),
        new FilterOption(EntryFilter.WithProblems, "With problems"),
    };

    // ---- Hooks for the window ----------------------------------------------------------------

    /// <summary>Set by the window: commits any half-finished edit in the grid.</summary>
    public Action? CommitPendingEdits { get; set; }
    public event EventHandler? LanguagesChanged;
    public event EventHandler<EntryViewModel>? BringIntoViewRequested;
    public event EventHandler<CellViewModel>? FocusCellRequested;

    // ---- Commands ----------------------------------------------------------------------------

    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand AddKeyCommand { get; }
    public RelayCommand RenameKeyCommand { get; }
    public RelayCommand DeleteKeyCommand { get; }
    public RelayCommand AddLanguageCommand { get; }
    public RelayCommand NextMissingCommand { get; }
    public RelayCommand AutofillCommand { get; }
    public RelayCommand HideProblemsCommand { get; }
    public RelayCommand DismissExternalChangeCommand { get; }

    // ---- State -------------------------------------------------------------------------------

    public bool IsProjectLoaded => _set != null;
    public bool IsLoading => _isLoading;
    public bool IsReady => _set != null && !_isLoading;
    public bool ShowEmptyState => _set == null && !_isLoading;
    public string LoadingText => _loadingText;

    /// <summary>Explains on the start screen why the last file wasn't reopened.</summary>
    public string? StartupNotice => _startupNotice;
    public bool HasChanges => Languages.Any(l => l.IsDirty);
    public string WindowTitle => _set == null ? "ResxStudio" : $"{_set.BaseName}{(HasChanges ? " *" : "")} - ResxStudio";
    public string? FolderText => _set?.Folder;
    public string StatusText => _set == null ? "" : $"{Entries.Count:N0} keys, {EntriesView.Count:N0} shown";

    public string SaveStateText
    {
        get
        {
            if (_set == null) return "";
            var dirty = Languages.Where(l => l.IsDirty).Select(l => l.FileName).ToList();
            return dirty.Count == 0 ? "All changes saved" : "Unsaved: " + string.Join(", ", dirty);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    public FilterOption SelectedFilter
    {
        get => _filter;
        set
        {
            if (value != null && Set(ref _filter, value)) RefreshView();
        }
    }

    public GroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (Set(ref _selectedGroup, value) && !_rebuildingGroups) RefreshView();
        }
    }

    public EntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!Set(ref _selectedEntry, value)) return;
            RebuildCells();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Shown as a banner when a file changed outside the editor.</summary>
    public string? ExternalChangeMessage => _externalChangeMessage;

    // ---- Problems ----------------------------------------------------------------------------

    public bool ShowProblems
    {
        get => _showProblems;
        set => Set(ref _showProblems, value);
    }

    public ProblemCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (Set(ref _selectedCategory, value)) OnPropertyChanged(nameof(VisibleIssues));
        }
    }

    public IReadOnlyList<IssueViewModel> VisibleIssues => _selectedCategory?.Issues ?? (IReadOnlyList<IssueViewModel>)Array.Empty<IssueViewModel>();

    public IssueViewModel? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (Set(ref _selectedIssue, value) && value != null) NavigateTo(value);
        }
    }

    public int ErrorCount => _issues.Count(i => i.Severity == IssueSeverity.Error);
    public int WarningCount => _issues.Count(i => i.Severity == IssueSeverity.Warning);
    public int NoteCount => _issues.Count(i => i.Severity == IssueSeverity.Info);
    public int ProblemCount => ErrorCount + WarningCount;
    public string ProblemsButtonText => ProblemCount == 0 ? "Problems" : $"Problems ({ProblemCount:N0})";
    public string ProblemSummary =>
        $"{Plural(ErrorCount, "error")}, {Plural(WarningCount, "warning")}, {Plural(NoteCount, "note")}";

    // ---- Opening -----------------------------------------------------------------------------

    /// <summary>Runs once when the window has loaded.</summary>
    public async Task StartupAsync(string? commandLinePath, bool skipReopen)
    {
        if (commandLinePath != null)
        {
            await TryOpenAsync(commandLinePath);
            return;
        }

        // The last launch didn't finish opening its file: it crashed or froze. Don't repeat that.
        if (_settings.OpeningFile is { } unfinished)
        {
            Log.Write($"The previous launch didn't finish opening {unfinished}; not reopening it.");
            _settings.OpeningFile = null;
            _settings.Save();
            SetStartupNotice($"ResxStudio didn't finish opening {Path.GetFileName(unfinished)} last time, so it wasn't reopened automatically. " +
                             $"If it happens again, please send {Log.FilePath}.");
            return;
        }

        if (skipReopen)
        {
            Log.Write("Shift was held at startup; not reopening the last file.");
            return;
        }

        if (_settings.LastFile is { } path && File.Exists(path)) await TryOpenAsync(path);
    }

    public async Task<bool> TryOpenAsync(string path)
    {
        if (_isLoading) return false;
        SetLoading(true, $"Opening {Path.GetFileName(path)}…");
        SetStartupNotice(null);
        _settings.OpeningFile = path;
        _settings.Save();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Log.Step($"Reading {path}");
            // Parsing XML and building the rows happens off the UI thread, so the window stays responsive.
            var project = await Task.Run(() => LoadProject(path));
            Log.Write($"Read {project.Languages.Count} files and {project.Entries.Count} keys in {stopwatch.ElapsedMilliseconds} ms");

            Log.Step("Showing the keys");
            ApplyProject(project, path);

            // Wait until the grid has been laid out and drawn once.
            await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Log.Step($"Ready after {stopwatch.ElapsedMilliseconds} ms");

            _settings.LastFile = path;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Opening {path} failed: {ex}");
            _dialogs.ShowError("Couldn't open the file", ex.Message);
            return false;
        }
        finally
        {
            _settings.OpeningFile = null;
            _settings.Save();
            SetLoading(false, "");
        }
    }

    /// <summary>Asks to save if needed. Returns false when the user cancels.</summary>
    public bool ConfirmDiscardOrSave()
    {
        CommitPendingEdits?.Invoke();
        if (!HasChanges) return true;
        return _dialogs.AskSaveChanges() switch
        {
            true => Save(),
            false => true,
            null => false,
        };
    }

    private async void OpenInteractive()
    {
        if (!ConfirmDiscardOrSave()) return;
        var path = _dialogs.PickResxFile();
        if (path != null) await TryOpenAsync(path);
    }

    private async void Reload()
    {
        if (_openedPath == null) return;
        CommitPendingEdits?.Invoke();
        if (HasChanges && !_dialogs.Confirm("Reload from disk", "Discard all unsaved changes and reload the files from disk?"))
            return;
        await ReloadPreservingStateAsync();
    }

    private sealed record LoadedProject(ResourceSetInfo Set, List<LanguageViewModel> Languages, List<EntryViewModel> Entries);

    /// <summary>Reads all files and builds the rows. Runs on a background thread; touches no UI.</summary>
    private LoadedProject LoadProject(string path)
    {
        var set = ResourceSetLocator.Locate(path);

        var languages = new List<LanguageViewModel> { new(null, ResxDocument.Load(set.NeutralPath)) };
        foreach (var file in set.Languages)
            languages.Add(new LanguageViewModel(file.Code, ResxDocument.Load(file.FilePath)));

        // Merge every file into one row per key. The default file decides the order;
        // keys that only exist in translations go at the end.
        var byKey = new Dictionary<string, EntryViewModel>(StringComparer.Ordinal);
        var ordered = new List<EntryViewModel>();
        foreach (var language in languages)
        {
            var strings = language.Document.ReadStrings();
            language.Baseline = ToDictionary(Normalize(strings, language.IsNeutral));
            foreach (var (key, value) in strings)
            {
                if (!byKey.TryGetValue(key, out var entry))
                {
                    entry = new EntryViewModel(key, OnValueChanged);
                    byKey[key] = entry;
                    ordered.Add(entry);
                }
                entry.Load(language.Id, value);
            }
        }

        // Restore "auto-translated, not reviewed yet" marks from earlier sessions.
        foreach (var (languageId, values) in ReviewStore.Load(set.NeutralPath))
            foreach (var (key, value) in values)
                if (byKey.TryGetValue(key, out var entry) && entry[languageId]?.Replace("\r\n", "\n") == value)
                    entry.MachineTranslated.Set(languageId, true);

        var hidden = _settings.HiddenLanguages.ToList();
        foreach (var language in languages)
            language.IsVisible = !hidden.Contains(language.Id, StringComparer.OrdinalIgnoreCase);

        return new LoadedProject(set, languages, ordered);
    }

    /// <summary>Puts a loaded project on screen. UI thread.</summary>
    private void ApplyProject(LoadedProject project, string path)
    {
        foreach (var old in Languages) old.PropertyChanged -= OnLanguagePropertyChanged;
        Languages.Clear();
        foreach (var language in project.Languages)
        {
            language.PropertyChanged += OnLanguagePropertyChanged;
            Languages.Add(language);
        }

        _set = project.Set;
        _openedPath = path;
        SelectedEntry = null;

        Entries = new ObservableCollection<EntryViewModel>(project.Entries);
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(StatusText));
        EntriesView = CreateView(Entries);

        _searchText = "";
        _filter = FilterOptions[0];
        _selectedGroup = null;
        _externalChangeMessage = null;
        _dismissedExternalChange = null;
        Groups.Clear();

        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(EntriesView));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedFilter));
        OnPropertyChanged(nameof(ExternalChangeMessage));

        var stopwatch = Stopwatch.StartNew();
        RecomputeStats();
        Log.Write($"Counted progress and checked {Entries.Count} keys in {stopwatch.ElapsedMilliseconds} ms ({_issues.Count} findings)");

        Log.Step("Building the grid columns");
        LanguagesChanged?.Invoke(this, EventArgs.Empty);
        RaiseProjectState();
    }

    /// <summary>Reloads from disk but keeps the search, filter, section and selected key.</summary>
    private async Task ReloadPreservingStateAsync()
    {
        if (_openedPath == null) return;
        var search = _searchText;
        var filter = _filter;
        var groupName = _selectedGroup is { IsAll: false } group ? group.Name : null;
        var selectedKey = _selectedEntry?.Key;

        if (!await TryOpenAsync(_openedPath)) return;

        _searchText = search;
        _filter = filter;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedFilter));
        if (groupName != null
            && Groups.FirstOrDefault(g => !g.IsAll && string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            _selectedGroup = match;
            OnPropertyChanged(nameof(SelectedGroup));
        }
        RefreshView();

        if (selectedKey != null && Entries.FirstOrDefault(e => e.Key == selectedKey) is { } entry && EntriesView.Contains(entry))
        {
            SelectedEntry = entry;
            BringIntoViewRequested?.Invoke(this, entry);
        }
    }

    private void SetLoading(bool loading, string text)
    {
        _isLoading = loading;
        _loadingText = text;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(ShowEmptyState));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetStartupNotice(string? notice)
    {
        _startupNotice = notice;
        OnPropertyChanged(nameof(StartupNotice));
    }

    // ---- Saving ------------------------------------------------------------------------------

    public bool Save()
    {
        CommitPendingEdits?.Invoke();
        var dirty = Languages.Where(l => l.IsDirty).ToList();
        if (dirty.Count == 0) return true;

        if (!ConfirmProblemsBeforeSave(dirty)) return false;

        // Did anyone else change these files since we read them?
        var changedOnDisk = dirty.Where(l => l.Document.CheckDisk() == DiskChange.Modified).ToList();
        var merge = false;
        if (changedOnDisk.Count > 0)
        {
            var names = string.Join(", ", changedOnDisk.Select(l => l.FileName));
            var pronoun = changedOnDisk.Count == 1 ? "it" : "them";
            var choice = _dialogs.Choose(
                "Files changed outside ResxStudio",
                $"{names} changed on disk after ResxStudio loaded {pronoun}, for example by git or Visual Studio.\n\n" +
                "Merge keeps those changes and applies only the keys you edited here. If both sides changed the same key, your version is kept.\n\n" +
                "Overwrite replaces the file with what you see here, and the other changes are lost.",
                "Merge", "Overwrite", "Cancel");
            if (choice is null or 2) return false;
            merge = choice == 0;
        }

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
            if (entry.OriginalKey != null && entry.OriginalKey != entry.Key)
                renames[entry.OriginalKey] = entry.Key;

        var failures = new List<string>();
        var conflicts = new List<string>();
        var merged = false;

        foreach (var language in dirty)
        {
            var desired = BuildDesired(language);
            try
            {
                if (merge && changedOnDisk.Contains(language))
                {
                    var fresh = ResxDocument.Load(language.Document.FilePath);
                    var theirs = Normalize(fresh.ReadStrings(), language.IsNeutral);
                    var keyConflicts = new List<string>();
                    var result = ResxMerger.Merge(desired, language.Baseline, theirs, renames, keyConflicts);
                    fresh.Apply(result, renames);
                    fresh.Save();
                    language.Document = fresh;
                    language.Baseline = ToDictionary(result);
                    conflicts.AddRange(keyConflicts.Select(k => $"{k} in {language.FileName}"));
                    merged = true;
                }
                else
                {
                    language.Document.Apply(desired, renames);
                    language.Document.Save();
                    language.Baseline = ToDictionary(desired);
                }
                language.IsDirty = false;
            }
            catch (Exception ex)
            {
                failures.Add($"{language.FileName}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            _dialogs.ShowError("Some files weren't saved", string.Join(Environment.NewLine, failures));
            return false;
        }

        foreach (var entry in Entries) entry.OriginalKey = entry.Key;
        PersistReviewState();

        if (merged)
        {
            if (conflicts.Count > 0)
            {
                var list = string.Join(Environment.NewLine, conflicts.Take(15));
                if (conflicts.Count > 15) list += $"{Environment.NewLine}and {conflicts.Count - 15} more";
                _dialogs.ShowInfo("Merged and saved. These keys were changed both here and on disk; your version was kept:" +
                                  Environment.NewLine + Environment.NewLine + list);
            }
            // Show the merged result, including the other side's changes.
            _ = ReloadPreservingStateAsync();
            return true;
        }

        CheckExternalChanges();
        RunValidation();
        return true;
    }

    /// <summary>Broken placeholders can crash string.Format at runtime, so ask before saving them.</summary>
    private bool ConfirmProblemsBeforeSave(IReadOnlyCollection<LanguageViewModel> dirty)
    {
        RunValidation();
        var dirtyIds = dirty.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var broken = _issues.Count(i =>
            i.Severity == IssueSeverity.Error
            && i.LanguageId != null && dirtyIds.Contains(i.LanguageId)
            && (i.Category == TranslationChecks.Placeholders || i.Category == TranslationChecks.Malformed));
        if (broken == 0) return true;

        var verb = broken == 1 ? "has" : "have";
        var choice = _dialogs.Choose(
            "Check placeholders before saving",
            $"{Plural(broken, "text")} {verb} missing or malformed placeholders such as {{0}}. " +
            "If the app formats these strings, it can show the wrong text or throw an error.",
            "Save anyway", "Show problems", "Cancel");

        if (choice == 0) return true;
        if (choice == 1)
        {
            ShowProblems = true;
            SelectedFilter = FilterOptions.First(f => f.Mode == EntryFilter.WithProblems);
        }
        return false;
    }

    private List<KeyValuePair<string, string>> BuildDesired(LanguageViewModel language)
    {
        var desired = new List<KeyValuePair<string, string>>();
        foreach (var entry in Entries)
        {
            var value = entry[language.Id];
            // An empty translation would hide the default text at runtime, so it's left out.
            if (value == null || (!language.IsNeutral && value.Length == 0)) continue;
            desired.Add(new KeyValuePair<string, string>(entry.Key, value.Replace("\r\n", "\n")));
        }
        return desired;
    }

    private static List<KeyValuePair<string, string>> Normalize(IEnumerable<KeyValuePair<string, string>> values, bool isNeutral)
        => values
            .Where(p => isNeutral || p.Value.Length > 0)
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value.Replace("\r\n", "\n")))
            .ToList();

    private static Dictionary<string, string> ToDictionary(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values) result[key] = value;
        return result;
    }

    /// <summary>Saves which values are still unreviewed machine translations (outside the .resx files).</summary>
    public void PersistReviewState()
    {
        if (_set == null) return;
        var data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (!entry.MachineTranslated.Any) continue;
            foreach (var language in Languages)
            {
                if (!entry.MachineTranslated[language.Id] || entry[language.Id] is not { } value) continue;
                if (!data.TryGetValue(language.Id, out var map))
                {
                    map = new Dictionary<string, string>(StringComparer.Ordinal);
                    data[language.Id] = map;
                }
                map[entry.Key] = value.Replace("\r\n", "\n");
            }
        }
        ReviewStore.Save(_set.NeutralPath, data);
    }

    // ---- Changes made outside the editor -----------------------------------------------------

    /// <summary>Called when the window is activated: shows a banner if a file changed on disk.</summary>
    public void CheckExternalChanges()
    {
        if (_set == null || _isLoading) return;

        var changed = Languages
            .Where(l => l.Document.CheckDisk() is DiskChange.Modified or DiskChange.Deleted)
            .ToList();

        string? message = null;
        if (changed.Count > 0)
        {
            var names = string.Join(", ", changed.Select(l => l.FileName));
            var verb = changed.Count == 1 ? "was" : "were";
            message = changed.Any(l => l.IsDirty)
                ? $"{names} {verb} changed outside ResxStudio. When you save, you can merge your edits with those changes, or reload now to discard your edits."
                : $"{names} {verb} changed outside ResxStudio. Reload to see the latest version.";
        }

        if (message != null && message == _dismissedExternalChange) message = null;
        if (message == _externalChangeMessage) return;
        _externalChangeMessage = message;
        OnPropertyChanged(nameof(ExternalChangeMessage));
    }

    private void DismissExternalChange()
    {
        _dismissedExternalChange = _externalChangeMessage;
        _externalChangeMessage = null;
        OnPropertyChanged(nameof(ExternalChangeMessage));
    }

    // ---- Autofill ----------------------------------------------------------------------------

    private void Autofill()
    {
        CommitPendingEdits?.Invoke();
        var targets = Languages.Where(l => !l.IsNeutral).ToList();
        if (targets.Count == 0)
        {
            _dialogs.ShowInfo("Add a language first, then autofill can translate into it.");
            return;
        }

        var viewEntries = EntriesView.Cast<EntryViewModel>().ToList();
        _dialogs.ShowAutofill(new AutofillContext(targets, Entries.ToList(), viewEntries, SelectedEntry, _settings));

        _settings.Save();
        PersistReviewState();
        RecomputeStats();
        RefreshView();
    }

    // ---- Keys --------------------------------------------------------------------------------

    private void AddKey()
    {
        CommitPendingEdits?.Invoke();

        var prefixSource = _selectedGroup is { IsAll: false } group ? group.Name : _selectedEntry?.Group;
        var prefix = prefixSource != null && prefixSource != EntryViewModel.NoGroup ? prefixSource + "." : "";

        var key = _dialogs.Prompt("Add key", "Key name", prefix, "Add key", text => ValidateKey(text, null));
        if (key == null) return;

        var entry = new EntryViewModel(key, OnValueChanged) { OriginalKey = null };
        entry.Load(LanguageViewModel.NeutralId, "");

        // Insert alphabetically so the files stay sorted.
        var index = Entries.Count;
        for (var i = 0; i < Entries.Count; i++)
        {
            if (string.Compare(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase) > 0)
            {
                index = i;
                break;
            }
        }
        Entries.Insert(index, entry);
        Languages[0].IsDirty = true;

        RecomputeStats();
        if (!PassesFilter(entry)) ResetFilters();

        SelectedEntry = entry;
        BringIntoViewRequested?.Invoke(this, entry);
        if (SelectedCells.Count > 0) FocusCellRequested?.Invoke(this, SelectedCells[0]);
    }

    private void RenameKey()
    {
        var entry = SelectedEntry;
        if (entry == null) return;
        CommitPendingEdits?.Invoke();

        var key = _dialogs.Prompt("Rename key", "New name (renamed in every language file)", entry.Key, "Rename",
            text => ValidateKey(text, entry));
        if (key == null || key == entry.Key) return;

        entry.Key = key;
        MarkDirtyWhereSet(entry);
        RecomputeStats();
        RefreshView();
        SelectedEntry = entry;
        BringIntoViewRequested?.Invoke(this, entry);
    }

    private void DeleteKey()
    {
        var entry = SelectedEntry;
        if (entry == null) return;
        CommitPendingEdits?.Invoke();

        var fileCount = Languages.Count(l => entry[l.Id] != null);
        var files = fileCount == 1 ? "1 file" : $"{fileCount} files";
        if (!_dialogs.Confirm("Delete key", $"Delete \"{entry.Key}\" and its text from {files}?")) return;

        var viewIndex = EntriesView.IndexOf(entry);
        MarkDirtyWhereSet(entry);
        Entries.Remove(entry);
        RecomputeStats();

        SelectedEntry = EntriesView.Count == 0
            ? null
            : (EntryViewModel)EntriesView.GetItemAt(Math.Clamp(viewIndex, 0, EntriesView.Count - 1));
    }

    private InputCheck ValidateKey(string text, EntryViewModel? self)
    {
        var key = text.Trim();
        if (key.Length == 0) return new(false, "Enter a key name.");
        if (key.EndsWith('.')) return new(false, "A key can't end with a dot.");
        if (Entries.Any(e => !ReferenceEquals(e, self) && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)))
            return new(false, "A key with this name already exists.");
        if (key.Any(char.IsWhiteSpace))
            return new(true, "Keys with spaces work, but generated code can't turn them into property names.");
        return new(true, "");
    }

    private void MarkDirtyWhereSet(EntryViewModel entry)
    {
        foreach (var language in Languages)
            if (entry[language.Id] != null) language.IsDirty = true;
    }

    // ---- Languages ---------------------------------------------------------------------------

    private void AddLanguage()
    {
        if (_set == null) return;
        CommitPendingEdits?.Invoke();

        var input = _dialogs.Prompt("Add language", "Culture code, for example fr, es-MX or zh-Hans", "", "Add language", ValidateCulture);
        if (input == null) return;

        var code = CultureInfo.GetCultureInfo(input).Name;
        var path = Path.Combine(_set.Folder, $"{_set.BaseName}{_set.Separator}{code}.resx");
        var language = new LanguageViewModel(code, ResxDocument.CreateFromTemplate(Languages[0].Document, path))
        {
            IsDirty = true,
        };
        language.PropertyChanged += OnLanguagePropertyChanged;

        var index = 1;
        while (index < Languages.Count && string.Compare(Languages[index].Id, code, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        Languages.Insert(index, language);

        RecomputeStats();
        RebuildCells();
        if (_filter.Mode != EntryFilter.All) RefreshView();
        LanguagesChanged?.Invoke(this, EventArgs.Empty);
        RaiseProjectState();
    }

    private InputCheck ValidateCulture(string text)
    {
        var code = text.Trim();
        if (code.Length == 0) return new(false, "Enter a culture code.");
        if (!ResourceSetLocator.IsCulture(code)) return new(false, "That isn't a culture code .NET recognizes.");

        var culture = CultureInfo.GetCultureInfo(code);
        if (Languages.Any(l => string.Equals(l.Id, culture.Name, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"{culture.EnglishName} is already open.");

        var fileName = $"{_set!.BaseName}{_set.Separator}{culture.Name}.resx";
        if (File.Exists(Path.Combine(_set.Folder, fileName)))
            return new(false, $"{fileName} already exists in the folder.");

        return new(true, $"Creates {fileName} for {culture.EnglishName} ({culture.NativeName}) when you save.");
    }

    private void OnLanguagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LanguageViewModel language) return;

        if (e.PropertyName == nameof(LanguageViewModel.IsDirty))
        {
            RaiseProjectState();
        }
        else if (e.PropertyName == nameof(LanguageViewModel.IsVisible))
        {
            _settings.HiddenLanguages.RemoveAll(id => string.Equals(id, language.Id, StringComparison.OrdinalIgnoreCase));
            if (!language.IsVisible) _settings.HiddenLanguages.Add(language.Id);
            _settings.Save();

            RebuildCells();
            if (_filter.Mode != EntryFilter.All) RefreshView();
            RefreshGroups();
        }
    }

    // ---- Navigation --------------------------------------------------------------------------

    private void GoToNextMissing()
    {
        CommitPendingEdits?.Invoke();
        var count = EntriesView.Count;
        if (count == 0) return;

        var start = SelectedEntry == null ? -1 : EntriesView.IndexOf(SelectedEntry);
        for (var step = 1; step <= count; step++)
        {
            var entry = (EntryViewModel)EntriesView.GetItemAt((start + step) % count);
            if (!IsIncomplete(entry)) continue;

            SelectedEntry = entry;
            BringIntoViewRequested?.Invoke(this, entry);
            var cell = SelectedCells.FirstOrDefault(c => c.IsMissing);
            if (cell != null) FocusCellRequested?.Invoke(this, cell);
            return;
        }

        _dialogs.ShowInfo("Every key in this view has text in all the visible languages.");
    }

    private void NavigateTo(IssueViewModel issue)
    {
        var entry = Entries.FirstOrDefault(e => e.Key == issue.Key);
        if (entry == null) return;

        var language = issue.Issue.LanguageId == null
            ? null
            : Languages.FirstOrDefault(l => string.Equals(l.Id, issue.Issue.LanguageId, StringComparison.OrdinalIgnoreCase));
        if (language is { IsVisible: false }) language.IsVisible = true;

        if (!EntriesView.Contains(entry)) ResetFilters();

        SelectedEntry = entry;
        BringIntoViewRequested?.Invoke(this, entry);
        var cell = SelectedCells.FirstOrDefault(c => c.Language == language) ?? SelectedCells.FirstOrDefault();
        if (cell != null) FocusCellRequested?.Invoke(this, cell);
    }

    private void ResetFilters()
    {
        _searchText = "";
        _filter = FilterOptions[0];
        _selectedGroup = Groups.FirstOrDefault();
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedFilter));
        OnPropertyChanged(nameof(SelectedGroup));
        RefreshView();
    }

    // ---- Filtering and stats -----------------------------------------------------------------

    private ListCollectionView CreateView(ObservableCollection<EntryViewModel> source)
        => new(source) { Filter = PassesFilter };

    private bool PassesFilter(object item)
    {
        var entry = (EntryViewModel)item;

        if (_selectedGroup is { IsAll: false } group
            && !string.Equals(entry.Group, group.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        switch (_filter.Mode)
        {
            case EntryFilter.Untranslated when !IsIncomplete(entry):
            case EntryFilter.AutoTranslated when !HasVisibleMachineTranslation(entry):
            case EntryFilter.WithProblems when !_problemKeys.Contains(entry.Key):
                return false;
        }

        var query = _searchText.Trim();
        if (query.Length == 0) return true;
        if (entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var language in Languages)
            if (entry[language.Id]?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    /// <summary>Missing text in the default file or in any visible language.</summary>
    private bool IsIncomplete(EntryViewModel entry)
    {
        foreach (var language in Languages)
            if ((language.IsNeutral || language.IsVisible) && !entry.HasValue(language.Id))
                return true;
        return false;
    }

    private bool HasVisibleMachineTranslation(EntryViewModel entry)
    {
        if (!entry.MachineTranslated.Any) return false;
        foreach (var language in Languages)
            if (language.IsVisible && entry.MachineTranslated[language.Id])
                return true;
        return false;
    }

    private void RefreshView()
    {
        CommitPendingEdits?.Invoke();
        if (EntriesView.IsAddingNew) EntriesView.CommitNew();
        if (EntriesView.IsEditingItem) EntriesView.CommitEdit();
        EntriesView.Refresh();
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnValueChanged(EntryViewModel entry, string languageId)
    {
        var language = Languages.FirstOrDefault(l => string.Equals(l.Id, languageId, StringComparison.OrdinalIgnoreCase));
        if (language != null) language.IsDirty = true;
        _statsTimer.Stop();
        _statsTimer.Start();
    }

    private void RecomputeStats()
    {
        const string neutral = LanguageViewModel.NeutralId;
        var keysInDefault = Entries.Count(e => e[neutral] != null);

        foreach (var language in Languages)
        {
            if (language.IsNeutral)
                language.SetStats(Entries.Count(e => e.HasValue(neutral)), Entries.Count);
            else
                language.SetStats(Entries.Count(e => e[neutral] != null && e.HasValue(language.Id)), keysInDefault);
        }

        RefreshGroups();
        RunValidation();
        OnPropertyChanged(nameof(StatusText));
    }

    private void RefreshGroups()
    {
        var stats = new SortedDictionary<string, (int Count, int Missing)>(StringComparer.OrdinalIgnoreCase);
        var allMissing = 0;
        foreach (var entry in Entries)
        {
            var incomplete = IsIncomplete(entry);
            stats.TryGetValue(entry.Group, out var s);
            stats[entry.Group] = (s.Count + 1, s.Missing + (incomplete ? 1 : 0));
            if (incomplete) allMissing++;
        }

        var names = stats.Keys.ToList();
        var unchanged = Groups.Count == names.Count + 1
            && Groups.Skip(1).Select(g => g.Name).SequenceEqual(names, StringComparer.OrdinalIgnoreCase);

        if (!unchanged)
        {
            var previous = _selectedGroup;
            _rebuildingGroups = true;
            try
            {
                Groups.Clear();
                Groups.Add(new GroupViewModel("", isAll: true));
                foreach (var name in names) Groups.Add(new GroupViewModel(name, isAll: false));

                _selectedGroup = previous is { IsAll: false }
                    ? Groups.FirstOrDefault(g => !g.IsAll && string.Equals(g.Name, previous.Name, StringComparison.OrdinalIgnoreCase)) ?? Groups[0]
                    : Groups[0];
                OnPropertyChanged(nameof(SelectedGroup));
            }
            finally
            {
                _rebuildingGroups = false;
            }

            // The selected section disappeared, so the filter changed.
            if (previous is { IsAll: false } && _selectedGroup.IsAll) RefreshView();
        }

        Groups[0].Update(Entries.Count, allMissing);
        foreach (var group in Groups.Skip(1))
        {
            var s = stats[group.Name];
            group.Update(s.Count, s.Missing);
        }
    }

    // ---- Validation --------------------------------------------------------------------------

    private void RunValidation()
    {
        var issues = BuildIssues();
        _problemKeys = issues
            .Where(i => i.Severity != IssueSeverity.Info)
            .Select(i => i.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (!issues.SequenceEqual(_issues))
        {
            _issues = issues;
            RebuildProblemCategories();
        }

        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(NoteCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(ProblemsButtonText));
        OnPropertyChanged(nameof(ProblemSummary));
    }

    private List<ValidationIssue> BuildIssues()
    {
        var issues = new List<ValidationIssue>();
        if (_set == null) return issues;

        const string neutral = LanguageViewModel.NeutralId;
        var defaultLanguage = Languages[0];

        // The same key twice in one file: .NET throws when reading it, and only one copy can be edited.
        foreach (var language in Languages)
            foreach (var key in language.Document.DuplicateKeys)
                issues.Add(new ValidationIssue(IssueSeverity.Warning, TranslationChecks.Duplicates, key, language.Id, language.Header,
                    $"Appears more than once in {language.FileName}. Saving keeps a single copy with the text shown here."));

        // Keys that differ only by case collide in generated code and case-insensitive lookups.
        foreach (var group in Entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var entry in group)
            {
                var others = string.Join(", ", group.Where(e => e != entry).Select(e => e.Key));
                issues.Add(new ValidationIssue(IssueSeverity.Warning, TranslationChecks.Duplicates, entry.Key, null, "All files",
                    $"Differs from {others} only by upper/lower case. Generated code and some lookups treat them as the same key."));
            }
        }

        foreach (var entry in Entries)
        {
            var defaultText = entry[neutral];
            if (defaultText == null)
            {
                var withText = Languages.Where(l => !l.IsNeutral && entry[l.Id] != null).ToList();
                if (withText.Count > 0)
                    issues.Add(new ValidationIssue(IssueSeverity.Error, TranslationChecks.Orphans, entry.Key, neutral,
                        string.Join(", ", withText.Select(l => l.EnglishName)),
                        "Has no default text, so languages without a translation show nothing. Add default text, or rename the key if it's a typo."));
                continue;
            }

            foreach (var problem in TranslationChecks.CheckDefault(defaultText))
                issues.Add(new ValidationIssue(problem.Severity, problem.Category, entry.Key, neutral, defaultLanguage.Header, problem.Message));

            foreach (var language in Languages)
            {
                if (language.IsNeutral) continue;
                var text = entry[language.Id];
                if (string.IsNullOrEmpty(text))
                {
                    if (defaultText.Length > 0)
                        issues.Add(new ValidationIssue(IssueSeverity.Info, TranslationChecks.Missing, entry.Key, language.Id, language.Header, "No translation yet."));
                    continue;
                }
                foreach (var problem in TranslationChecks.CheckTranslation(defaultText, text))
                    issues.Add(new ValidationIssue(problem.Severity, problem.Category, entry.Key, language.Id, language.Header, problem.Message));
            }
        }

        issues.Sort((a, b) =>
        {
            var c = a.Severity.CompareTo(b.Severity);
            if (c != 0) return c;
            c = string.Compare(a.Category, b.Category, StringComparison.Ordinal);
            if (c != 0) return c;
            c = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Location, b.Location, StringComparison.Ordinal);
        });
        return issues;
    }

    /// <summary>One entry per problem type, most severe first, each holding a flat list of its issues.</summary>
    private void RebuildProblemCategories()
    {
        var previous = _selectedCategory?.Name;
        var categories = _issues
            .GroupBy(i => i.Category)
            .Select(g => new ProblemCategoryViewModel(
                g.Key,
                g.Min(i => i.Severity),
                g.Select(i => new IssueViewModel(i)).ToList()))
            .OrderBy(c => c.Severity)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        ProblemCategories.Clear();
        foreach (var category in categories) ProblemCategories.Add(category);

        _selectedIssue = null;
        _selectedCategory = categories.FirstOrDefault(c => c.Name == previous) ?? categories.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedIssue));
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(VisibleIssues));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private void RebuildCells()
    {
        foreach (var cell in SelectedCells) cell.Dispose();
        SelectedCells.Clear();
        if (_selectedEntry == null) return;

        foreach (var language in Languages)
            if (language.IsNeutral || language.IsVisible)
                SelectedCells.Add(new CellViewModel(_selectedEntry, language));
    }

    private void RaiseProjectState()
    {
        OnPropertyChanged(nameof(IsProjectLoaded));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(FolderText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";
}
