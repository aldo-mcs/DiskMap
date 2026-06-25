using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Scanning.Mft;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NtfsVolumeDataBuffer
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

/// <summary>Thin, exception-throwing wrappers around the Win32 calls needed to read a volume's $MFT directly.</summary>
internal static class NtfsInterop
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    public static SafeFileHandle OpenVolume(string driveLetter)
    {
        // driveLetter like "C" -> "\\.\C:"
        string path = $@"\\.\{driveLetter}:";
        var handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 5) // ERROR_ACCESS_DENIED
                throw new UnauthorizedAccessException($"Administrator privileges are required to read {path} directly.");
            throw new IOException($"Could not open volume {path}: Win32 error {error}");
        }
        return new SafeFileHandle(handle, ownsHandle: true);
    }

    public static NtfsVolumeDataBuffer GetVolumeData(SafeFileHandle volume)
    {
        int size = Marshal.SizeOf<NtfsVolumeDataBuffer>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = DeviceIoControl(volume, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero);
            if (!ok)
                throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA failed: Win32 error {Marshal.GetLastWin32Error()}");
            return Marshal.PtrToStructure<NtfsVolumeDataBuffer>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
