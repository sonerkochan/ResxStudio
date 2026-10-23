using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ResxStudio.Models;
using ResxStudio.Services;

namespace ResxStudio.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _statsTimer;

    private ResourceSetInfo? _set;
    private string? _openedPath;
    private string _searchText = "";
    private bool _showMissingOnly;
    private GroupViewModel? _selectedGroup;
    private EntryViewModel? _selectedEntry;
    private bool _rebuildingGroups;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        EntriesView = CreateView(Entries);

        // Search waits for a short pause in typing so filtering 20k+ strings never feels laggy.
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshView(); };

        // Progress and section counts are recalculated shortly after edits, not on every keystroke.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _statsTimer.Tick += (_, _) => { _statsTimer.Stop(); RecomputeStats(); };

        OpenCommand = new RelayCommand(OpenInteractive);
        SaveCommand = new RelayCommand(() => Save(), () => HasChanges);
        ReloadCommand = new RelayCommand(Reload, () => IsProjectLoaded);
        AddKeyCommand = new RelayCommand(AddKey, () => IsProjectLoaded);
        RenameKeyCommand = new RelayCommand(RenameKey, () => SelectedEntry != null);
        DeleteKeyCommand = new RelayCommand(DeleteKey, () => SelectedEntry != null);
        AddLanguageCommand = new RelayCommand(AddLanguage, () => IsProjectLoaded);
        NextMissingCommand = new RelayCommand(GoToNextMissing, () => IsProjectLoaded);
    }

    // ---- Collections -------------------------------------------------------------------------

    public ObservableCollection<LanguageViewModel> Languages { get; } = new();
    public ObservableCollection<EntryViewModel> Entries { get; private set; } = new();
    public ListCollectionView EntriesView { get; private set; }
    public ObservableCollection<GroupViewModel> Groups { get; } = new();
    public ObservableCollection<CellViewModel> SelectedCells { get; } = new();

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

    // ---- State -------------------------------------------------------------------------------

    public bool IsProjectLoaded => _set != null;
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

    public bool ShowMissingOnly
    {
        get => _showMissingOnly;
        set
        {
            if (Set(ref _showMissingOnly, value)) RefreshView();
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

    // ---- Opening -----------------------------------------------------------------------------

    public bool TryOpen(string path)
    {
        try
        {
            Open(path);
            _settings.LastFile = path;
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Couldn't open the file", ex.Message);
            return false;
        }
    }

    public void OpenLastFile()
    {
        if (_settings.LastFile is { } path && File.Exists(path)) TryOpen(path);
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

    private void OpenInteractive()
    {
        if (!ConfirmDiscardOrSave()) return;
        var path = _dialogs.PickResxFile();
        if (path != null) TryOpen(path);
    }

    private void Reload()
    {
        if (_openedPath == null) return;
        CommitPendingEdits?.Invoke();
        if (HasChanges && !_dialogs.Confirm("Reload from disk", "Discard all unsaved changes and reload the files from disk?"))
            return;
        TryOpen(_openedPath);
    }

    private void Open(string path)
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
            foreach (var (key, value) in language.Document.ReadStrings())
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

        foreach (var old in Languages) old.PropertyChanged -= OnLanguagePropertyChanged;
        Languages.Clear();
        foreach (var language in languages)
        {
            language.IsVisible = !_settings.HiddenLanguages.Contains(language.Id, StringComparer.OrdinalIgnoreCase);
            language.PropertyChanged += OnLanguagePropertyChanged;
            Languages.Add(language);
        }

        _set = set;
        _openedPath = path;
        SelectedEntry = null;

        Entries = new ObservableCollection<EntryViewModel>(ordered);
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(StatusText));
        EntriesView = CreateView(Entries);

        _searchText = "";
        _showMissingOnly = false;
        _selectedGroup = null;
        Groups.Clear();

        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(EntriesView));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(ShowMissingOnly));

        RecomputeStats();
        LanguagesChanged?.Invoke(this, EventArgs.Empty);
        RaiseProjectState();
    }

    // ---- Saving ------------------------------------------------------------------------------

    public bool Save()
    {
        CommitPendingEdits?.Invoke();

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
            if (entry.OriginalKey != null && entry.OriginalKey != entry.Key)
                renames[entry.OriginalKey] = entry.Key;

        var failures = new List<string>();
        foreach (var language in Languages.Where(l => l.IsDirty).ToList())
        {
            var desired = new List<KeyValuePair<string, string>>();
            foreach (var entry in Entries)
            {
                var value = entry[language.Id];
                // An empty translation would hide the default text at runtime, so it's left out.
                if (value == null || (!language.IsNeutral && value.Length == 0)) continue;
                desired.Add(new KeyValuePair<string, string>(entry.Key, value.Replace("\r\n", "\n")));
            }

            try
            {
                language.Document.Apply(desired, renames);
                language.Document.Save();
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
        return true;
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

        if (!PassesFilter(entry))
        {
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
            _selectedGroup = Groups.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedGroup));
            RefreshView();
        }

        RecomputeStats();
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
        if (_showMissingOnly) RefreshView();
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
            if (_showMissingOnly) RefreshView();
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

    // ---- Filtering and stats -----------------------------------------------------------------

    private ListCollectionView CreateView(ObservableCollection<EntryViewModel> source)
        => new(source) { Filter = PassesFilter };

    private bool PassesFilter(object item)
    {
        var entry = (EntryViewModel)item;

        if (_selectedGroup is { IsAll: false } group
            && !string.Equals(entry.Group, group.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_showMissingOnly && !IsIncomplete(entry)) return false;

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
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(FolderText));
        CommandManager.InvalidateRequerySuggested();
    }
}
