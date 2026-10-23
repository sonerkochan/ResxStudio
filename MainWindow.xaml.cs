using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ResxStudio.Services;
using ResxStudio.ViewModels;
using ResxStudio.Views;

namespace ResxStudio;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<LanguageViewModel, DataGridColumn> _columns = new();
    private bool _gridEditing;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(this) { CommitPendingEdits = CommitGridEdits };
        _vm.LanguagesChanged += (_, _) => RebuildColumns();
        _vm.BringIntoViewRequested += (_, entry) =>
            Dispatcher.InvokeAsync(() => EntriesGrid.ScrollIntoView(entry), DispatcherPriority.Background);
        _vm.FocusCellRequested += (_, cell) => FocusCell(cell);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowProblems)) UpdateProblemsPanel();
        };
        DataContext = _vm;

        // Track whether a grid cell is being edited, so edits are only committed when there is one.
        EntriesGrid.BeginningEdit += (_, _) => _gridEditing = true;
        EntriesGrid.RowEditEnding += (_, _) => Dispatcher.InvokeAsync(() => _gridEditing = false);

        Loaded += OnLoaded;
        Closing += OnClosing;
        // Coming back to the window after git pull, VS, etc.: look for files changed on disk.
        // Skipped while one of our own dialogs is open, since closing it also activates the window.
        Activated += (_, _) =>
        {
            if (OwnedWindows.Count == 0) _vm.CheckExternalChanges();
        };
        Log.Write("Main window constructed");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var commandLinePath = args.Length > 1 && File.Exists(args[1]) ? args[1] : null;
        var skipReopen = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        await _vm.StartupAsync(commandLinePath, skipReopen);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscardOrSave())
        {
            e.Cancel = true;
            return;
        }
        _vm.PersistReviewState();
    }

    // ---- Problems panel ----------------------------------------------------------------------

    private GridLength _problemsHeight = new(240);

    private void UpdateProblemsPanel()
    {
        if (_vm.ShowProblems)
        {
            ProblemsRow.MinHeight = 100;
            ProblemsRow.Height = _problemsHeight;
            ProblemsSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (ProblemsRow.ActualHeight > 0) _problemsHeight = new GridLength(ProblemsRow.ActualHeight);
            ProblemsRow.MinHeight = 0;
            ProblemsRow.Height = new GridLength(0);
            ProblemsSplitter.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Grid columns ------------------------------------------------------------------------

    private void RebuildColumns()
    {
        foreach (var language in _columns.Keys) language.PropertyChanged -= OnLanguageChanged;
        _columns.Clear();
        EntriesGrid.Columns.Clear();

        EntriesGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Key",
            Binding = new Binding(nameof(EntryViewModel.Key)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            Width = new DataGridLength(300),
            SortMemberPath = nameof(EntryViewModel.Key),
            ElementStyle = (Style)FindResource("KeyCellText"),
        });

        foreach (var language in _vm.Languages)
        {
            var path = $"[{language.Id}]";
            var column = new DataGridTextColumn
            {
                Header = language.Header,
                Binding = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                // Fixed widths (resizable by dragging) keep layout simple and fast with many languages.
                Width = new DataGridLength(320),
                MinWidth = 120,
                CanUserSort = false,
                ElementStyle = (Style)FindResource("ValueCellText"),
                EditingElementStyle = (Style)FindResource("ValueCellEditor"),
                CellStyle = BuildValueCellStyle(language.Id),
                Visibility = language.IsVisible ? Visibility.Visible : Visibility.Collapsed,
            };
            _columns[language] = column;
            language.PropertyChanged += OnLanguageChanged;
            EntriesGrid.Columns.Add(column);
        }
    }

    /// <summary>Cell style: rose when this language has no text, amber while it's an unreviewed machine translation.</summary>
    private Style BuildValueCellStyle(string languageId)
    {
        var style = new Style(typeof(DataGridCell), (Style)FindResource("CellBase"));
        foreach (var emptyValue in new object?[] { null, "" })
        {
            var missing = new DataTrigger { Binding = new Binding($"[{languageId}]"), Value = emptyValue };
            missing.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("MissingBrush")));
            style.Triggers.Add(missing);
        }

        var machine = new DataTrigger
        {
            Binding = new Binding($"{nameof(EntryViewModel.MachineTranslated)}[{languageId}]"),
            Value = true,
        };
        machine.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("MachineBrush")));
        style.Triggers.Add(machine);
        return style;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LanguageViewModel.IsVisible)
            && sender is LanguageViewModel language
            && _columns.TryGetValue(language, out var column))
        {
            column.Visibility = language.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void CommitGridEdits()
    {
        if (!_gridEditing) return;
        try
        {
            EntriesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch (InvalidOperationException)
        {
            // Nothing to commit.
        }
    }

    // Clicking outside the grid doesn't end a cell edit on its own; do it here.
    private void OnGridLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_gridEditing) return;
        if (e.NewFocus is Visual target && EntriesGrid.IsAncestorOf(target)) return;
        Dispatcher.InvokeAsync(CommitGridEdits);
    }

    private void FocusCell(CellViewModel cell)
    {
        Dispatcher.InvokeAsync(() =>
        {
            CellsList.UpdateLayout();
            if (CellsList.ItemContainerGenerator.ContainerFromItem(cell) is FrameworkElement container
                && FindDescendant<TextBox>(container) is { } box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
                container.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnCopyKey(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedEntry == null) return;
        try
        {
            Clipboard.SetText(_vm.SelectedEntry.Key);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy in another app; ignore.
        }
    }

    // ---- Keyboard and drag-and-drop ----------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.SearchText = "";
            e.Handled = true;
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (GetDroppedResx(e) == null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedResx(e);
        if (path == null) return;
        e.Handled = true;
        if (_vm.ConfirmDiscardOrSave()) _ = _vm.TryOpenAsync(path);
    }

    private static string? GetDroppedResx(DragEventArgs e)
        => e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            : null;

    // ---- IDialogService ----------------------------------------------------------------------

    public string? PickResxFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open the default .resx file",
            Filter = "Resource files (*.resx)|*.resx",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? Prompt(string title, string label, string initialText, string confirmText, Func<string, InputCheck> validate)
    {
        var dialog = new InputDialog(title, label, initialText, confirmText, validate) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public int? Choose(string title, string message, params string[] options)
    {
        var dialog = new ChoiceDialog(title, message, options) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Choice : null;
    }

    public void ShowAutofill(AutofillContext context)
    {
        var dialog = new AutofillDialog(context) { Owner = this };
        dialog.ShowDialog();
    }

    public bool Confirm(string title, string message)
        => MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public bool? AskSaveChanges()
        => MessageBox.Show(this, "Save your changes first?", "Unsaved changes",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
            {
                MessageBoxResult.Yes => true,
                MessageBoxResult.No => false,
                _ => null,
            };

    public void ShowError(string title, string message)
        => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message)
        => MessageBox.Show(this, message, "ResxStudio", MessageBoxButton.OK, MessageBoxImage.Information);
}
