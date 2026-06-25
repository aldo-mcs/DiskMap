using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DiskMap.App.Infrastructure;
using DiskMap.App.ViewModels;
using DiskMap.App.Views;

namespace DiskMap.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.ScrollToRowRequested += OnScrollToRow;
        _vm.ShowHistoryRequested += OnShowHistory;
        _vm.ConfirmRequested += (title, message, confirmText) => ConfirmDialog.Show(this, title, message, confirmText);
        Treemap.DrillRequested += (_, node) => _vm.DrillInto(node);
        Sunburst.DrillRequested += (_, node) => _vm.DrillInto(node);
        Tree.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));

        InputBindings.Add(new KeyBinding(_vm.ScanFolderCommand, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.RescanCommand, Key.F5, ModifierKeys.None));

        StateChanged += OnStateChanged;
        WindowChromeHelper.ApplyDwmPolish(this);
        // MainWindow outlives a live theme switch (unlike dialogs, which are reconstructed fresh
        // each time), so its title bar needs an explicit refresh when the theme changes underneath it.
        ThemeManager.ThemeChanged += () => WindowChromeHelper.ApplyDwmPolish(this);

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateAdvancedColumns(_vm.IsAdvancedMode);
    }

    // GridViewColumn isn't a FrameworkElement, so it can't inherit DataContext or bind
    // Visibility directly — toggle Width instead, the standard workaround for hiding columns.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAdvancedMode))
            UpdateAdvancedColumns(_vm.IsAdvancedMode);
    }

    private void UpdateAdvancedColumns(bool advanced)
    {
        CreatedColumn.Width = advanced ? 125 : 0;
        AttrColumn.Width = advanced ? 55 : 0;
        SlackColumn.Width = advanced ? 80 : 0;
        MftIndexColumn.Width = advanced ? 75 : 0;
        HardLinksColumn.Width = advanced ? 50 : 0;
        StreamsColumn.Width = advanced ? 60 : 0;
    }

    // ---------- custom title bar ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : ""; // Segoe MDL2: restore / maximize
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
        RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
    }

    // Constrain maximize to the monitor work area (so it doesn't cover the taskbar / clip edges).
    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT work = info.rcWork, mon = info.rcMonitor;
                    mmi.ptMaxPosition.X = work.left - mon.left;
                    mmi.ptMaxPosition.Y = work.top - mon.top;
                    mmi.ptMaxSize.X = work.right - work.left;
                    mmi.ptMaxSize.Y = work.bottom - work.top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- view-model wiring ----------

    private void OnScrollToRow(NodeViewModel row) =>
        Dispatcher.BeginInvoke(() => Tree.ScrollIntoView(row), System.Windows.Threading.DispatcherPriority.Background);

    private void OnShowHistory(string? root)
    {
        var window = new HistoryWindow(_vm.Store, root) { Owner = this };
        window.ShowDialog();
    }

    private void OnShowAbout(object sender, RoutedEventArgs e)
    {
        var window = new AboutWindow { Owner = this };
        window.ShowDialog();
    }

    private void OnShowWelcome(object sender, RoutedEventArgs e)
    {
        var window = new WelcomeWindow { Owner = this };
        window.ShowDialog();
    }

    private void OnShowSettings(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = this };
        window.ShowDialog();
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: string header } && !string.IsNullOrEmpty(header))
            _vm.SortBy(header);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnSelectTreemapView(object sender, RoutedEventArgs e) => _vm.IsSunburstView = false;
    private void OnSelectSunburstView(object sender, RoutedEventArgs e) => _vm.IsSunburstView = true;

    private void OnThemeSystem(object sender, RoutedEventArgs e) => ThemeManager.SetTheme(AppTheme.System);
    private void OnThemeLight(object sender, RoutedEventArgs e) => ThemeManager.SetTheme(AppTheme.Light);
    private void OnThemeDark(object sender, RoutedEventArgs e) => ThemeManager.SetTheme(AppTheme.Dark);

    private void OnFocusSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // ---------- interop ----------

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
