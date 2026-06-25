using System.Diagnostics;
using System.Windows;
using DiskMap.App.Infrastructure;

namespace DiskMap.App.Views;

public partial class WelcomeWindow : Window
{
    private const string GitHubSponsorsUrl = "https://github.com/sponsors/aldo-mcs";
    private const string StripeDonateUrl = "https://buy.stripe.com/fZu28sd6m7qneKHbmpejK01";

    public WelcomeWindow()
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDwmPolish(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnGitHubSponsor(object sender, RoutedEventArgs e) => OpenUrl(GitHubSponsorsUrl);

    private void OnStripeDonate(object sender, RoutedEventArgs e) => OpenUrl(StripeDonateUrl);

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
