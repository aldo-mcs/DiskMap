using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DiskMap.Core.Actions;

/// <summary>
/// In-app cleanup actions operating on real paths. Deletion goes to the Windows Recycle Bin
/// (recoverable) via SHFileOperation — never a permanent delete.
/// </summary>
public static class FileActions
{
    public static void RevealInExplorer(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void OpenCommandPromptHere(string directoryPath) =>
        Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = directoryPath, UseShellExecute = true });

    /// <summary>Sends one or more files/directories to the Recycle Bin. Returns true on success.</summary>
    public static bool SendToRecycleBin(IEnumerable<string> paths, bool confirm = false)
    {
        // SHFileOperation expects a double-null-terminated, null-separated list of paths.
        string joined = string.Join('\0', paths) + "\0\0";

        ushort flags = FOF_ALLOWUNDO | FOF_NOERRORUI | FOF_WANTNUKEWARNING;
        if (!confirm) flags |= FOF_NOCONFIRMATION;

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = joined,
            fFlags = flags,
        };

        int result = SHFileOperationW(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    public static bool SendToRecycleBin(string path, bool confirm = false) =>
        SendToRecycleBin([path], confirm);

    /// <summary>Compresses a file or directory into a .zip placed next to it, returning the archive path.</summary>
    public static string Archive(string path)
    {
        if (Directory.Exists(path))
        {
            string zip = UniquePath(path.TrimEnd('\\', '/') + ".zip");
            ZipFile.CreateFromDirectory(path, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
            return zip;
        }
        else
        {
            string zip = UniquePath(Path.ChangeExtension(path, ".zip"));
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
            return zip;
        }
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;
        string dir = Path.GetDirectoryName(desired)!;
        string name = Path.GetFileNameWithoutExtension(desired);
        string ext = Path.GetExtension(desired);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // --- SHFileOperation interop ---

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);
}
