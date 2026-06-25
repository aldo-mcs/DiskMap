namespace DiskMap.Core.Cleanup;

/// <summary>
/// Scans well-known, universally-safe-to-clear temp/cache locations — bounded, known folders,
/// not a drive walk, so this finishes in well under a second on a typical machine.
/// </summary>
public static class CleanupScanner
{
    public static List<CleanupCategoryResult> Scan()
    {
        var results = new List<CleanupCategoryResult>
        {
            ScanFolders(CleanupCategoryKind.WindowsTemp, "Windows Temp Files",
                "Temporary files created by Windows and installers. Safe to delete; recreated as needed.",
                [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")]),

            ScanFolders(CleanupCategoryKind.UserTemp, "User Temp Files",
                "Temporary files apps create for your account. Safe to delete; recreated as needed.",
                [Path.GetTempPath()]),

            ScanRecycleBin(),

            ScanFolders(CleanupCategoryKind.BrowserCache, "Browser Cache",
                "Cached web content from Edge, Chrome, and Firefox. Safe to delete; pages just reload it.",
                GetBrowserCachePaths()),

            ScanFolders(CleanupCategoryKind.WindowsUpdateCache, "Windows Update Cache",
                "Downloaded update installers Windows keeps after updates are already applied.",
                [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download")]),

            ScanThumbnailCache(),

            ScanFolders(CleanupCategoryKind.ErrorReports, "Error Reports & Crash Dumps",
                "Diagnostic files saved after app crashes. Safe to delete once you're done troubleshooting.",
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
                ]),
        };

        return results;
    }

    private static CleanupCategoryResult ScanFolders(
        CleanupCategoryKind kind, string name, string description, IReadOnlyList<string> folders)
    {
        long size = 0, count = 0;
        var existing = new List<string>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            existing.Add(folder);
            foreach (var file in CleanupFileWalker.EnumerateFiles(folder))
            {
                try { size += new FileInfo(file).Length; count++; }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return new CleanupCategoryResult(kind, name, description, size, count, existing);
    }

    private static CleanupCategoryResult ScanThumbnailCache()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");

        var files = new List<string>();
        if (Directory.Exists(folder))
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "thumbcache_*.db"));
                files.AddRange(Directory.EnumerateFiles(folder, "iconcache_*.db"));
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        long size = 0;
        foreach (var f in files)
        {
            try { size += new FileInfo(f).Length; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return new CleanupCategoryResult(CleanupCategoryKind.ThumbnailCache, "Thumbnail Cache",
            "Cached preview thumbnails. Safe to delete; Explorer regenerates them as needed.",
            size, files.Count, files);
    }

    private static CleanupCategoryResult ScanRecycleBin()
    {
        var (size, count) = RecycleBinInterop.Query();
        return new CleanupCategoryResult(CleanupCategoryKind.RecycleBin, "Recycle Bin",
            "Files you've already deleted. Emptying is permanent.", size, count, []);
    }

    private static List<string> GetBrowserCachePaths()
    {
        var paths = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string edge = Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache");
        if (Directory.Exists(edge)) paths.Add(edge);

        string chrome = Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache");
        if (Directory.Exists(chrome)) paths.Add(chrome);

        string firefoxProfiles = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxProfiles))
        {
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(firefoxProfiles))
                {
                    string cache2 = Path.Combine(profile, "cache2");
                    if (Directory.Exists(cache2)) paths.Add(cache2);
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return paths;
    }
}
