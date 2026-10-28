using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ResxStudio.Services;

namespace ResxStudio.Views;

/// <summary>Single-line prompt with live validation, used for keys and culture codes.</summary>
public partial class InputDialog : Window
{
    private readonly Func<string, InputCheck> _validate;
    private bool _touched;

    public InputDialog(string title, string label, string initialText, string confirmText, Func<string, InputCheck> validate)
    {
        InitializeComponent();
        _validate = validate;

        Title = title;
        LabelText.Text = label;
        OkButton.Content = confirmText;
        InputBox.Text = initialText;
        _touched = false;
        Validate();

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        };
    }

    public string? Result { get; private set; }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _touched = true;
        Validate();
    }

    private void Validate()
    {
        var check = _validate(InputBox.Text);
        OkButton.IsEnabled = check.IsValid;
        MessageText.Text = check.Message;
        // Don't shout about an empty box before the user has typed anything.
        var showAsError = !check.IsValid && _touched;
        MessageText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, showAsError ? "DangerBrush" : "MutedBrush");
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_validate(InputBox.Text).IsValid) return;
        Result = InputBox.Text.Trim();
        DialogResult = true;
    }
}
