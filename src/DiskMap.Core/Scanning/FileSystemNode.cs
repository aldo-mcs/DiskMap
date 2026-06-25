namespace DiskMap.Core.Scanning;

/// <summary>
/// A node in the scanned file-system tree. One instance per file or directory.
/// Sizes and counts are aggregated bottom-up for directories.
/// </summary>
public sealed class FileSystemNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }

    /// <summary>Logical size in bytes (sum of file lengths for directories).</summary>
    public long Size { get; set; }

    /// <summary>Physical size on disk in bytes (cluster-rounded).</summary>
    public long AllocatedSize { get; set; }

    /// <summary>Number of files (non-directories) in this subtree.</summary>
    public long FileCount { get; set; }

    public DateTime LastModified { get; init; }

    /// <summary>Creation time, when available from the scan source (advanced-mode detail).</summary>
    public DateTime Created { get; init; }

    /// <summary>Raw Win32 FILE_ATTRIBUTE_* flags, when available (advanced-mode detail).</summary>
    public uint Attributes { get; init; }

    /// <summary>This file's own position in the volume's $MFT, when scanned that way. Null for
    /// recursively-scanned nodes — there's no equivalent concept on that path.</summary>
    public long? MftRecordIndex { get; init; }

    /// <summary>Hard link count from the MFT record header. 0 means "not collected" (recursive
    /// scans don't have this for free — reading it would cost a per-file handle open) rather
    /// than "zero links", since every real file has at least one.</summary>
    public int HardLinkCount { get; init; }

    /// <summary>Count of named alternate data streams (e.g. Zone.Identifier) beyond the file's
    /// primary content — invisible in Explorer, only available from an MFT scan.</summary>
    public int AlternateStreamCount { get; init; }

    public bool IsReparsePoint => (Attributes & 0x400) != 0;

    /// <summary>True when the memory-saver trimmed this directory's children to its largest, discarding the
    /// rest to bound peak memory on huge drives. The aggregate (Size/Allocated/FileCount) stays accurate;
    /// only the visible child list is reduced. The tree shows a "…N more" indicator when set.</summary>
    public bool Pruned { get; set; }

    public FileSystemNode? Parent { get; set; }

    public List<FileSystemNode> Children { get; } = [];

    /// <summary>Lower-cased extension including the dot (e.g. ".mp4"), or "" for files without one / directories.</summary>
    public string Extension =>
        IsDirectory ? string.Empty : System.IO.Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>Fraction (0..1) of the parent directory's size that this node occupies.</summary>
    public double FractionOfParent =>
        Parent is { Size: > 0 } p ? (double)Size / p.Size : (Parent is null ? 1.0 : 0.0);

    public IEnumerable<FileSystemNode> EnumerateFiles()
    {
        if (!IsDirectory)
        {
            yield return this;
            yield break;
        }
        foreach (var child in Children)
            foreach (var file in child.EnumerateFiles())
                yield return file;
    }
}
