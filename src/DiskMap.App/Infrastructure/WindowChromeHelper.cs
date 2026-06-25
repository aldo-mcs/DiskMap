using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiskMap.App.Infrastructure;

/// <summary>Shared DWM polish so every custom-chrome window looks consistent: dark native
/// frame (for any residual non-client pixels) and Windows 11 rounded corners (falls back to
/// square, unchanged, on Windows 10 — the attribute is simply unsupported there).</summary>
internal static class WindowChromeHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void ApplyDwmPolish(Window window)
    {
        void Apply()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int dark = ThemeManager.IsEffectivelyDark() ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
