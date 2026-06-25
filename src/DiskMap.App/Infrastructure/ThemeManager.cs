using System.Diagnostics;
using System.Windows;
using DiskMap.Core.Settings;
using Microsoft.Win32;

namespace DiskMap.App.Infrastructure;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Applies the Light/Dark palette + shared styles. Supports both live switching (runtime palette swap
/// without restart) and the legacy restart path. All XAML references to palette keys use
/// <c>DynamicResource</c> so they update automatically when the palette dictionary is swapped.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>True when the resolved theme produces a dark background.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the theme has been applied or switched, e.g. so chrome that can't use
    /// DynamicResource (DWM title bar polish) can refresh itself.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Applies the given theme to the running application's resources. Call once at startup,
    /// before any window is constructed. Can also be called at runtime for live switching.</summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        IsDark = IsEffectivelyDark(theme);

        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;

        var palette = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "Themes/Palette.Dark.xaml" : "Themes/Palette.Light.xaml", UriKind.Relative)
        };

        if (dictionaries.Count == 0)
        {
            // Palette must be added before Styles.xaml/Icons.xaml — both reference palette colors via
            // StaticResource, which resolves at parse/add time, not lazily.
            dictionaries.Add(palette);
            dictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/Styles.xaml", UriKind.Relative) });
            dictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/Icons.xaml", UriKind.Relative) });
        }
        else
        {
            // A plain indexer replace (dictionaries[0] = palette) does not reliably propagate to
            // already-rendered DynamicResource consumers. Remove-then-insert does.
            dictionaries.RemoveAt(0);
            dictionaries.Insert(0, palette);
        }

        ThemeChanged?.Invoke();
    }

    /// <summary>Live-switches the theme (no restart). Persists the choice and swaps the palette dictionary
    /// so all <c>DynamicResource</c> bindings update immediately.</summary>
    public static void SetTheme(AppTheme theme)
    {
        var settings = AppSettings.Load();
        settings.Theme = theme.ToString();
        settings.Save();

        Apply(theme);
    }

    /// <summary>Legacy path: persists the chosen theme and restarts the app so every window picks it up cleanly.
    /// Kept as a fallback but <see cref="SetTheme"/> is preferred — it's instant and doesn't lose window state.</summary>
    public static void SetThemeAndRestart(AppTheme theme)
    {
        var settings = AppSettings.Load();
        settings.Theme = theme.ToString();
        settings.Save();

        string exePath = Process.GetCurrentProcess().MainModule!.FileName;
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    public static bool IsEffectivelyDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsSystemDarkMode(),
    };

    public static bool IsEffectivelyDark() => IsEffectivelyDark(Current);

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 0;
        }
        catch (Exception) { /* registry unavailable; fall back below */ }
        return true; // dark by default if the setting can't be read
    }

    public static AppTheme ParseOrDefault(string? value) =>
        Enum.TryParse<AppTheme>(value, out var theme) ? theme : AppTheme.System;
}
