using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ResxStudio.Views;

/// <summary>A message with a row of buttons. The first option is the default; Escape closes without a choice.</summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string message, IReadOnlyList<string> options)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var button = new Button { Content = options[i], MinWidth = 90 };
            if (i == 0)
            {
                button.Style = (Style)FindResource("PrimaryButton");
                button.IsDefault = true;
            }
            button.Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0);
            button.Click += (_, _) =>
            {
                Choice = index;
                DialogResult = true;
            };
            ButtonsPanel.Children.Add(button);
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Choice = null;
            DialogResult = false;
            e.Handled = true;
        };
        Loaded += (_, _) => (ButtonsPanel.Children.Count > 0 ? ButtonsPanel.Children[0] : this).Focus();
    }

    public int? Choice { get; private set; }
}
