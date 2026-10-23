using System;

namespace ResxStudio.Services;

/// <summary>Everything the view model needs to ask the user. Implemented by MainWindow.</summary>
public interface IDialogService
{
    string? PickResxFile();
    string? Prompt(string title, string label, string initialText, string confirmText, Func<string, InputCheck> validate);
    bool Confirm(string title, string message);
    /// <summary>true = save, false = discard, null = cancel.</summary>
    bool? AskSaveChanges();
    void ShowError(string title, string message);
    void ShowInfo(string message);
}
