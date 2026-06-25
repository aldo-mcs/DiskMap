using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Long-path (<c>\\?\</c>) helpers. Paths beyond ~260 chars throw <see cref="PathTooLongException"/> under the
/// legacy Win32 path limit unless they carry the <c>\\?\</c> prefix, which opts into the 32K-char limit. The .NET
/// runtime has <c>LongPathsEnabled</c> appsetting too, but relying on it requires a per-machine registry change, so
/// we prefix explicitly — that works regardless of OS config.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LongPath
{
    private const string Prefix = @"\\?\";
    private const int SoftLimit = 248; // leave headroom under 260 for child segments appended by enumeration

    /// <summary>Returns the path with the <c>\\?\</c> prefix added when it's long enough to risk the legacy limit,
    /// or already prefixed. Relative paths and UNC paths are passed through unchanged (UNC needs <c>\\?\UNC\</c>).</summary>
    public static string EnsureExtended(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path;
        if (!Path.IsPathRooted(path)) return path;
        // UNC \\server\share\... -> \\?\UNC\server\share\...
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.AsSpan(2).ToString();
        return path.Length >= SoftLimit ? Prefix + path : path;
    }

    /// <summary>Strips a leading <c>\\?\</c> or <c>\\?\UNC\</c> prefix for display, since users don't want to see it.</summary>
    public static string StripExtended(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.AsSpan(8).ToString();
        if (path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return path.AsSpan(4).ToString();
        return path;
    }
}

/// <summary>Interop for <c>GetFileInformationByHandle</c>, used to read the per-volume file ID of a directory.
/// File IDs are how cycle detection works without trusting path strings — a junction that loops A→B→A has the
/// same file ID at both names.</summary>
[SupportedOSPlatform("windows")]
internal static class FileIdInterop
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    /// <summary>Tries to read (volume serial, file index) for a directory. Returns false on any failure — callers
    /// treat "couldn't read ID" as "don't add to the visited set" so the scan still proceeds.</summary>
    public static bool TryGetFileId(string path, out ulong key)
    {
        key = 0;
        try
        {
            // FILE_FLAG_BACKUP_SEMANTICS lets us open *directories* (a plain FileStream open fails on folders),
            // and doesn't require elevation. This is the only handle open we do per-directory for cycle detection.
            const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, (FileOptions)FILE_FLAG_BACKUP_SEMANTICS);
            if (!GetFileInformationByHandle(fs.SafeFileHandle, out var info)) return false;
            // Pack volume serial (high) + 64-bit file index (low) into a stable per-volume key.
            key = ((ulong)info.VolumeSerialNumber << 40) ^ info.FileIndex;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;

        // Combine the two 32-bit halves into one 64-bit index (NTFS file IDs fit in 64 bits).
        public ulong FileIndex => ((ulong)FileIndexHigh << 32) | FileIndexLow;
    }
}
