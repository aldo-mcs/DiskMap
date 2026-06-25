using System.IO;
using System.Windows;
using DiskMap.App.Infrastructure;
using DiskMap.Core.Actions;
using DiskMap.Core.Settings;

namespace DiskMap.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow()
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDwmPolish(this);

        _settings = AppSettings.Load();

        var themeRadio = ThemeManager.ParseOrDefault(_settings.Theme) switch
        {
            AppTheme.Light => ThemeLightRadio,
            AppTheme.Dark => ThemeDarkRadio,
            _ => ThemeSystemRadio,
        };
        themeRadio.IsChecked = true;

        var scanRadio = _settings.ScanMode switch
        {
            ScanImpactMode.Fast => ScanFastRadio,
            ScanImpactMode.Low => ScanLowRadio,
            _ => ScanBalancedRadio,
        };
        scanRadio.IsChecked = true;

        var reparseRadio = _settings.ReparseBehavior switch
        {
            ReparseBehavior.Ignore => ReparseIgnoreRadio,
            ReparseBehavior.Follow => ReparseFollowRadio,
            _ => ReparseShowRadio,
        };
        reparseRadio.IsChecked = true;

        MinDuplicateSizeBox.Text = Formatting.Bytes(_settings.MinDuplicateSize);
        MemorySaverCheck.IsChecked = _settings.MemorySaver;
        CrashSafeResumeCheck.IsChecked = _settings.CrashSafeResume;
        AlwaysElevatedCheck.IsChecked = _settings.AlwaysElevated;
    }

    private void OnAlwaysElevatedChanged(object sender, RoutedEventArgs e) =>
        ElevationWarningText.Visibility = AlwaysElevatedCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var theme = ThemeLightRadio.IsChecked == true ? AppTheme.Light
            : ThemeDarkRadio.IsChecked == true ? AppTheme.Dark
            : AppTheme.System;

        _settings.Theme = theme.ToString();
        _settings.ScanMode = ScanFastRadio.IsChecked == true ? ScanImpactMode.Fast
            : ScanLowRadio.IsChecked == true ? ScanImpactMode.Low
            : ScanImpactMode.Balanced;
        _settings.ReparseBehavior = ReparseIgnoreRadio.IsChecked == true ? ReparseBehavior.Ignore
            : ReparseFollowRadio.IsChecked == true ? ReparseBehavior.Follow
            : ReparseBehavior.Show;
        if (Formatting.TryParseSize(MinDuplicateSizeBox.Text, out long minSize))
            _settings.MinDuplicateSize = minSize;
        _settings.MemorySaver = MemorySaverCheck.IsChecked == true;
        _settings.CrashSafeResume = CrashSafeResumeCheck.IsChecked == true;
        _settings.AlwaysElevated = AlwaysElevatedCheck.IsChecked == true;
        _settings.Save();

        // Live: applies immediately, no restart needed.
        ThemeManager.SetTheme(theme);

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap");
        Directory.CreateDirectory(folder);
        FileActions.RevealInExplorer(folder);
    }
}
