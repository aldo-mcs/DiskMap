namespace DiskMap.Core.Cleanup;

public readonly record struct CleanupResult(long BytesFreed, int FilesDeleted, int FailedCount);

/// <summary>
/// Deletes the contents of a scanned category. Temp/cache categories are deleted permanently
/// (they're regenerable junk, not user data — recycling gigabytes of cache would just bloat the
/// Recycle Bin); the Recycle Bin category itself is emptied via the shell API. Per-file failures
/// (locked/in-use files) are skipped and counted rather than aborting the whole operation.
/// </summary>
public static class CleanupCleaner
{
    public static CleanupResult Clean(CleanupCategoryResult category)
    {
        if (category.Kind == CleanupCategoryKind.RecycleBin)
        {
            long size = category.TotalSize;
            bool ok = RecycleBinInterop.Empty();
            return ok ? new CleanupResult(size, (int)category.FileCount, 0) : new CleanupResult(0, 0, 1);
        }

        long freed = 0;
        int deleted = 0, failed = 0;

        foreach (var path in category.Paths)
        {
            if (File.Exists(path))
            {
                if (TryDeleteFile(path, out long len)) { freed += len; deleted++; }
                else failed++;
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in CleanupFileWalker.EnumerateFiles(path))
                {
                    if (TryDeleteFile(file, out long len)) { freed += len; deleted++; }
                    else failed++;
                }
                CleanupFileWalker.RemoveEmptySubdirectoriesUnder(path);
            }
        }

        return new CleanupResult(freed, deleted, failed);
    }

    private static bool TryDeleteFile(string path, out long length)
    {
        length = 0;
        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            info.Delete();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
