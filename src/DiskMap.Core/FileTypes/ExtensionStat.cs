using DiskMap.Core.Scanning;

namespace DiskMap.Core.FileTypes;

public sealed class ExtensionStat
{
    public required string Extension { get; init; }
    public long TotalSize { get; set; }
    public long FileCount { get; set; }
}

/// <summary>A category-level rollup: "Video 42%", "Games 30%", etc. The user-facing analytics layer
/// that most disk analyzers lack — per-extension lists are too granular to be graspable.</summary>
public sealed class CategoryStat
{
    public required FileCategory Category { get; init; }
    public long TotalSize { get; init; }
    public long FileCount { get; init; }
    public double Fraction { get; init; }
    public string Label => FileCategoryMeta.Label(Category);
}

public static class ExtensionStatsBuilder
{
    /// <summary>Aggregates total size and file count per extension across the whole subtree, largest first.</summary>
    public static List<ExtensionStat> Build(FileSystemNode root)
    {
        var map = new Dictionary<string, ExtensionStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in root.EnumerateFiles())
        {
            string ext = string.IsNullOrEmpty(file.Extension) ? "(no extension)" : file.Extension;
            if (!map.TryGetValue(ext, out var stat))
            {
                stat = new ExtensionStat { Extension = ext };
                map[ext] = stat;
            }
            stat.TotalSize += file.Size;
            stat.FileCount++;
        }

        var list = new List<ExtensionStat>(map.Values);
        list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
        return list;
    }

    /// <summary>Aggregates size and file count per <see cref="FileCategory"/> across the whole subtree, largest first.
    /// A single pass — it does not retain references to nodes, so it's cheap to run on a huge tree and
    /// releases everything but the result list when it returns.</summary>
    public static List<CategoryStat> BuildCategories(FileSystemNode root)
    {
        var sizes = new Dictionary<FileCategory, (long Size, long Count)>();
        foreach (var file in root.EnumerateFiles())
        {
            var cat = Categorizer.RefineByPath(Categorizer.OfExtension(file.Extension), file.Path);
            var prev = sizes.GetValueOrDefault(cat);
            sizes[cat] = (prev.Size + file.Size, prev.Count + 1);
        }

        long total = sizes.Values.Sum(v => v.Size);
        var list = new List<CategoryStat>(sizes.Count);
        foreach (var (cat, val) in sizes)
        {
            list.Add(new CategoryStat
            {
                Category = cat,
                TotalSize = val.Size,
                FileCount = val.Count,
                Fraction = total > 0 ? (double)val.Size / total : 0,
            });
        }
        list.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));
        return list;
    }
}
