namespace DiskMap.Core.Scanning;

public readonly record struct ScanProgress(long FilesScanned, long BytesScanned, string CurrentPath);
