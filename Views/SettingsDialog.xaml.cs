using System.Windows;
using System.Windows.Controls;
using ResxStudio.Services;

namespace ResxStudio.Views;

/// <summary>App settings. Pick a theme, then Apply; Close without applying leaves everything as it was.</summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();

        // Select the current choice first, then listen, so setting it doesn't count as a change.
        (ThemeService.Mode switch
        {
            ThemeMode.Light => LightOption,
            ThemeMode.Dark => DarkOption,
            _ => SystemOption,
        }).IsChecked = true;

        foreach (var option in new[] { LightOption, DarkOption, SystemOption })
            option.Checked += (_, _) => ApplyButton.IsEnabled = SelectedMode != ThemeService.Mode;
    }

    private ThemeMode SelectedMode
        => LightOption.IsChecked == true ? ThemeMode.Light
         : DarkOption.IsChecked == true ? ThemeMode.Dark
         : ThemeMode.System;

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ThemeService.SetMode(SelectedMode);
        ApplyButton.IsEnabled = false;
    }
}
