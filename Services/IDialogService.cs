using System;
using ResxStudio.ViewModels;

namespace ResxStudio.Services;

/// <summary>Everything the view model needs to ask the user. Implemented by MainWindow.</summary>
public interface IDialogService
{
    string? PickResxFile();
    string? Prompt(string title, string label, string initialText, string confirmText, Func<string, InputCheck> validate);
    bool Confirm(string title, string message);
    /// <summary>true = save, false = discard, null = cancel.</summary>
    bool? AskSaveChanges();
    /// <summary>Shows one button per option; returns the index clicked, or null if the window was closed.</summary>
    int? Choose(string title, string message, params string[] options);
    void ShowAutofill(AutofillContext context);
    void ShowError(string title, string message);
    void ShowInfo(string message);
}
