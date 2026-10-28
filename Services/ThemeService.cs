using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ResxStudio.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Light / dark / follow-Windows theming. Every colour in the app comes from the shared brushes in
/// App.xaml and are referenced with DynamicResource, so switching theme just swaps those brushes;
/// everything repaints immediately, with no restart and no window rebuild.
/// </summary>
public static class ThemeService
{
    private sealed record Entry(object Key, string Light, string Dark);

    private static readonly Entry[] Palette =
    {
        //            key                         light      dark
        new("CanvasBrush",                        "#EEF1F4", "#14181D"),
        new("SurfaceBrush",                       "#FFFFFF", "#1C2128"),
        new("LineBrush",                          "#D8DEE5", "#323A44"),
        new("LineSoftBrush",                      "#EBEEF2", "#262D35"),
        new("InkBrush",                           "#1C2733", "#E6EAEE"),
        new("MutedBrush",                         "#5D6B7A", "#98A4B1"),
        new("HoverBrush",                         "#F3F6F8", "#252C35"),
        new("AccentBrush",                        "#0F6E78", "#4DB8C2"),
        new("AccentDarkBrush",                    "#0A5259", "#7FD0D8"),
        new("AccentSoftBrush",                    "#DFF0F1", "#17363A"),
        new("OnAccentBrush",                      "#FFFFFF", "#081D20"),
        new("MissingBrush",                       "#FDECEC", "#3A2023"),
        new("MissingBorderBrush",                 "#F2B8B5", "#7A3B3F"),
        new("DangerBrush",                        "#B42318", "#F47A70"),
        new("WarnBrush",                          "#A15C07", "#E3A33E"),
        new("MachineBrush",                       "#FFF6DB", "#38311A"),
        new("MachineBorderBrush",                 "#F0D58C", "#6E5C25"),
        new("NoticeBrush",                        "#FFF1D6", "#3A3119"),
        new("NoticeBorderBrush",                  "#EBC57A", "#7A6226"),

        // Default WPF templates (scrollbar corner, text-box fallbacks, ...) read these.
        new(SystemColors.ControlBrushKey,         "#EEF1F4", "#14181D"),
        new(SystemColors.WindowBrushKey,          "#FFFFFF", "#1C2128"),
        new(SystemColors.ControlTextBrushKey,     "#1C2733", "#E6EAEE"),
        new(SystemColors.WindowTextBrushKey,      "#1C2733", "#E6EAEE"),
    };

    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppSettings? _settings;
    private static bool _initialized;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>True when the dark palette is showing (either chosen, or Windows is in dark mode).</summary>
    public static bool IsDark { get; private set; }

    public static void Initialize(AppSettings settings)
    {
        _settings = settings;
        Mode = Parse(settings.Theme);

        if (!_initialized)
        {
            _initialized = true;
            // Give every window, including dialogs, a dark title bar when the theme is dark.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        Apply();
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    public static ThemeMode Parse(string? value)
        => Enum.TryParse<ThemeMode>(value, ignoreCase: true, out var mode) ? mode : ThemeMode.System;

    /// <summary>Switches theme now and remembers the choice.</summary>
    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        if (_settings != null)
        {
            _settings.Theme = mode.ToString();
            _settings.Save();
        }
        Apply();
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows changed its light/dark setting; only matters when we're following it.
        // This event arrives on a background thread.
        if (Mode != ThemeMode.System) return;
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;

        IsDark = Mode == ThemeMode.Dark || (Mode == ThemeMode.System && SystemUsesDarkTheme());

        foreach (var entry in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(IsDark ? entry.Dark : entry.Light);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            // Everything references these with DynamicResource, so swapping the object repaints it all.
            app.Resources[entry.Key] = brush;
        }

        foreach (Window window in app.Windows) ApplyTitleBar(window);
    }

    // ---- Dark title bar ------------------------------------------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = IsDark ? 1 : 0;
            // 20 is the documented id (Windows 10 20H1+ and 11); 19 is what older builds used.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
        catch (Exception)
        {
            // A light title bar is cosmetic; never let it break a window.
        }
    }
}
