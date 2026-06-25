using System.Windows;
using DiskMap.App.Infrastructure;
using DiskMap.App.Views;
using DiskMap.Core.Scanning.Mft;
using DiskMap.Core.Settings;

namespace DiskMap.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();

        if (settings.AlwaysElevated && !ElevationHelper.IsElevated())
        {
            ElevationHelper.RestartElevated();
            return; // RestartElevated exits this process; nothing below should run.
        }

        ThemeManager.Apply(ThemeManager.ParseOrDefault(settings.Theme));

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        if (!settings.HasSeenWelcome)
        {
            settings.HasSeenWelcome = true;
            settings.Save();
            // Deferred to "Loaded" priority: showing this dialog synchronously, before the main
            // window's HWND/owner relationship has finished settling from Show(), was landing it
            // behind the main window instead of in front of it.
            Dispatcher.BeginInvoke(new Action(() => new WelcomeWindow { Owner = main }.ShowDialog()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
