namespace DiskMap.Core.Cleanup;

public enum CleanupCategoryKind
{
    WindowsTemp,
    UserTemp,
    RecycleBin,
    BrowserCache,
    WindowsUpdateCache,
    ThumbnailCache,
    ErrorReports,
}

/// <summary>
/// One reclaimable-space category. <see cref="Paths"/> holds the concrete files/folders that
/// make it up — individual files for file-pattern categories (e.g. thumbnail cache), folders
/// whose *contents* (not the folder itself) get cleared for everything else.
/// </summary>
public sealed record CleanupCategoryResult(
    CleanupCategoryKind Kind,
    string Name,
    string Description,
    long TotalSize,
    long FileCount,
    IReadOnlyList<string> Paths);
