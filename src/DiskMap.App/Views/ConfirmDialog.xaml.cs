using System.Windows;
using DiskMap.App.Infrastructure;

namespace DiskMap.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDwmPolish(this);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Shows a themed Yes/No-style confirmation. Returns true only if the user confirmed.</summary>
    public static bool Show(Window owner, string title, string message, string confirmText = "OK", string cancelText = "Cancel")
    {
        var dialog = new ConfirmDialog { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;
        return dialog.ShowDialog() == true;
    }
}
