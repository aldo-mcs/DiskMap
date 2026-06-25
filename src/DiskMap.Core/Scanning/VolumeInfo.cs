using System.Runtime.InteropServices;

namespace DiskMap.Core.Scanning;

public static class VolumeInfo
{
    public static long GetClusterSize(string path)
    {
        string root = Path.GetPathRoot(path) ?? path;
        try
        {
            if (GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
                return (long)sectorsPerCluster * bytesPerSector;
        }
        catch { /* fall through */ }
        return 4096; // sensible default cluster size
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);
}
