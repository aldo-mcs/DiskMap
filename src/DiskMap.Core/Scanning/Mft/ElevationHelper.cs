using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DiskMap.Core.Scanning.Mft;

[SupportedOSPlatform("windows")]
public static class ElevationHelper
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Relaunches the current executable elevated (triggers a UAC prompt) and exits this instance.</summary>
    public static void RestartElevated()
    {
        string exePath = Process.GetCurrentProcess().MainModule!.FileName;
        var info = new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas" };
        Process.Start(info);
        Environment.Exit(0);
    }
}
