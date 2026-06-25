using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiskMap.Core.Settings;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Fast, parallel recursive directory scanner. Sibling subdirectories are walked concurrently via the
/// thread-pool; sizes, allocated (on-disk) sizes and file counts are aggregated bottom-up. Progress is
/// reported (throttled) as it goes.
///
/// Reliability characteristics: corruption-tolerant (skips unreadable files/dirs, logs them), long-path
/// aware (<c>\\?\</c> prefix), reparse-point configurable (ignore/show/follow), cycle-safe (file-ID visited
/// set), throttleable (Fast/Balanced/Low impact), resumable (checkpoint), and optionally memory-trimming.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DirectoryScanner
{
    private readonly ConcurrentDictionary<string, long> _clusterSizes = new(StringComparer.OrdinalIgnoreCase);

    private long _filesScanned;
    private long _bytesScanned;
    private long _reportCounter;
    private IProgress<ScanProgress>? _progress;
    private ScanOptions _options = new();
    private ScanErrorCollector? _errors;
    private ScanCheckpoint? _checkpoint;
    private Dictionary<string, ScanCheckpoint.DirEntry>? _resumeEntries; // cached once per scan
    private HashSet<ulong>? _visited; // null when cycle detection isn't needed (default Show/Ignore path)

    /// <summary>Aggregate of directories the scan skipped — populated during the scan, exposed for the UI to
    /// surface "47 files skipped" instead of silently dropping them.</summary>
    public ScanErrorCollector Errors => _errors ??= new ScanErrorCollector();

    public FileSystemNode Scan(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        _options = options ?? new ScanOptions();
        _errors ??= new ScanErrorCollector();
        _filesScanned = 0;
        _bytesScanned = 0;
        _reportCounter = 0;
        _clusterSizes.Clear();

        // Cycle detection is only meaningful if we might follow a reparse point (Follow). In Show/Ignore we
        // never recurse through a link, so a path cycle is impossible and we skip the per-dir handle cost.
        _visited = _options.ReparseBehavior == ReparseBehavior.Follow ? [] : null;

        // Resume: load any checkpoint for this root so completed subtrees are skipped on a re-scan.
        if (_options.EnableResume && !string.IsNullOrEmpty(rootPath))
        {
            _checkpoint = new ScanCheckpoint(rootPath);
            _resumeEntries = _checkpoint.LoadExisting();
        }
        else
        {
            _checkpoint = null;
            _resumeEntries = null;
        }

        var rootInfo = new DirectoryInfo(LongPath.EnsureExtended(rootPath));
        var root = CreateDirectoryNode(rootInfo, parent: null);

        // Apply low-impact throttling for the duration of the scan, restoring on exit.
        using (_options.ImpactMode == ScanImpactMode.Low ? LowImpactScope.Enter() : default)
        {
            int dop = AppSettings.DegreeOfParallelism(_options.ImpactMode);
            ScanDirectory(root, dop, cancellationToken);
        }

        // Final progress flush.
        _progress?.Report(new ScanProgress(Interlocked.Read(ref _filesScanned), Interlocked.Read(ref _bytesScanned), rootPath));

        if (_options.MemorySaver)
            TrimForMemorySaver(root);

        // A successful scan invalidates the checkpoint — a future scan of this root starts fresh.
        _checkpoint?.Clear();
        return root;
    }

    private void ScanDirectory(FileSystemNode node, int dop, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Resume: if this exact directory was already fully scanned in a prior run, restore its aggregate and
        // skip recursing. We still materialize it (and below, its children) so the tree is structurally intact.
        if (_checkpoint is { } cp && TryRestoreFromCheckpoint(node, cp, out _))
            return;

        // File-ID cycle detection — only active in Follow mode. Mark this directory visited; if we've seen
        // this ID before, we've hit a reparse loop and must not recurse.
        if (_visited is not null)
        {
            if (FileIdInterop.TryGetFileId(node.Path, out var key) && !_visited.Add(key))
            {
                _errors?.Record(node.Path); // treat the cycle as a skipped/reason-noted entry
                return;
            }
        }

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(node.Path).GetFileSystemInfos();
        }
        catch (UnauthorizedAccessException) { _errors?.Record(node.Path); return; }
        catch (IOException) { _errors?.Record(node.Path); return; }

        long clusterSize = GetClusterSize(node.Path);
        var subdirectories = new List<FileSystemNode>();
        long size = 0, allocated = 0, fileCount = 0;

        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo dir)
            {
                bool isReparse = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isReparse)
                {
                    // Ignore: skip entirely. Show: list as a node, don't recurse. Follow: recurse (the
                    // file-ID visited set above prevents infinite loops through A→B→A-style links).
                    if (_options.ReparseBehavior == ReparseBehavior.Ignore)
                        continue;

                    var linkNode = new FileSystemNode
                    {
                        Path = dir.FullName,
                        Name = dir.Name,
                        IsDirectory = true,
                        LastModified = SafeLastWrite(dir),
                        Created = SafeCreated(dir),
                        Attributes = SafeAttributes(dir),
                        Parent = node,
                    };
                    node.Children.Add(linkNode);

                    if (_options.ReparseBehavior == ReparseBehavior.Follow)
                    {
                        // Recurse into the link's target; cycle guard is the file-ID set.
                        ScanDirectory(linkNode, dop, cancellationToken);
                        size += linkNode.Size;
                        allocated += linkNode.AllocatedSize;
                        fileCount += linkNode.FileCount;
                    }
                    continue;
                }
                subdirectories.Add(CreateDirectoryNode(dir, node));
            }
            else if (entry is FileInfo file)
            {
                long len;
                try { len = file.Length; }
                catch (IOException) { _errors?.Record(file.FullName); continue; }
                catch (UnauthorizedAccessException) { _errors?.Record(file.FullName); continue; }

                long alloc = RoundUpToCluster(len, clusterSize);
                node.Children.Add(new FileSystemNode
                {
                    Path = file.FullName,
                    Name = file.Name,
                    IsDirectory = false,
                    Size = len,
                    AllocatedSize = alloc,
                    FileCount = 0,
                    LastModified = SafeLastWrite(file),
                    Created = SafeCreated(file),
                    Attributes = SafeAttributes(file),
                    Parent = node,
                });
                size += len;
                allocated += alloc;
                fileCount++;
            }
        }

        Interlocked.Add(ref _filesScanned, fileCount);
        Interlocked.Add(ref _bytesScanned, size);
        MaybeReportProgress(node.Path);

        // Recurse into real subdirectories in parallel, bounded by the impact mode's DOP.
        if (subdirectories.Count > 0)
        {
            Parallel.ForEach(
                subdirectories,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = dop },
                subdir => ScanDirectory(subdir, dop, cancellationToken));
        }

        node.Children.AddRange(subdirectories);

        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
            {
                size += child.Size;
                allocated += child.AllocatedSize;
                fileCount += child.FileCount;
            }
        }

        node.Size = size;
        node.AllocatedSize = allocated;
        node.FileCount = fileCount;

        // Largest first — useful default ordering for both tree and treemap.
        node.Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        // Record + periodically flush this directory's completed aggregate so a crash or cancel can resume.
        if (_checkpoint is { } cp2)
        {
            cp2.RecordCompleted(node.Path, node.Size, node.AllocatedSize, node.FileCount);
            cp2.MaybeFlush();
        }
    }

    /// <summary>True if <paramref name="node"/>'s directory was already fully scanned in a prior run, and the
    /// checkpoint's aggregate has been applied to it. Returns false (and leaves the node untouched) otherwise.
    /// Reads from the in-memory <see cref="_resumeEntries"/> loaded once at scan start.</summary>
    private bool TryRestoreFromCheckpoint(FileSystemNode node, ScanCheckpoint cp, out bool restored)
    {
        restored = false;
        var existing = _resumeEntries;
        if (existing is null || existing.Count == 0) return false;
        if (existing.TryGetValue(node.Path, out var entry))
        {
            node.Size = entry.Size;
            node.AllocatedSize = entry.Allocated;
            node.FileCount = entry.Files;
            restored = true;
            return true; // skip recursing — subtree totals are known from the checkpoint
        }
        return false;
    }

    /// <summary>Memory saver: after aggregation, drop the children of any directory with more than
    /// <paramref name="keep"/> entries the user would have to expand to see — keep the largest, note the trim.
    /// Default keep is generous (256) so it only bites on genuinely huge folders.</summary>
    private static void TrimForMemorySaver(FileSystemNode root, int keep = 256)
    {
        var stack = new Stack<FileSystemNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsDirectory) continue;
            if (n.Children.Count > keep)
            {
                n.Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
                n.Children.RemoveRange(keep, n.Children.Count - keep);
                n.Pruned = true;
            }
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    private void MaybeReportProgress(string currentPath)
    {
        if (_progress is null) return;
        // Report roughly every 64 directories processed to avoid flooding the UI thread.
        if ((Interlocked.Increment(ref _reportCounter) & 0x3F) != 0) return;
        _progress.Report(new ScanProgress(Interlocked.Read(ref _filesScanned), Interlocked.Read(ref _bytesScanned), currentPath));
    }

    private static FileSystemNode CreateDirectoryNode(DirectoryInfo dir, FileSystemNode? parent) => new()
    {
        Path = dir.FullName,
        Name = string.IsNullOrEmpty(dir.Name) ? dir.FullName : dir.Name,
        IsDirectory = true,
        LastModified = SafeLastWrite(dir),
        Created = SafeCreated(dir),
        Attributes = SafeAttributes(dir),
        Parent = parent,
    };

    private static DateTime SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private static DateTime SafeCreated(FileSystemInfo info)
    {
        try { return info.CreationTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private static uint SafeAttributes(FileSystemInfo info)
    {
        try { return (uint)info.Attributes; }
        catch { return 0; }
    }

    private static long RoundUpToCluster(long size, long clusterSize)
    {
        if (clusterSize <= 0) return size;
        if (size == 0) return 0;
        return (size + clusterSize - 1) / clusterSize * clusterSize;
    }

    private long GetClusterSize(string path)
    {
        string root = Path.GetPathRoot(path) ?? path;
        return _clusterSizes.GetOrAdd(root, static r => VolumeInfo.GetClusterSize(r));
    }

    /// <summary>Scoped setter for <c>PROCESS_MODE_BACKGROUND_BEGIN</c> + below-normal priority, used only in
    /// Low impact mode so a background scan doesn't compete with the user's foreground work. Restored on
    /// dispose. Struct so the default value is a no-op (used when not in Low mode).</summary>
    [SupportedOSPlatform("windows")]
    private readonly struct LowImpactScope : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly uint _oldPriority;

        private LowImpactScope(IntPtr handle, uint oldPriority)
        {
            _handle = handle;
            _oldPriority = oldPriority;
        }

        public static LowImpactScope Enter()
        {
            IntPtr h = GetCurrentProcess();
            uint old = GetPriorityClass(h);
            // PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000; tells the OS to use background I/O priority.
            SetPriorityClass(h, 0x00100000);
            return new LowImpactScope(h, old);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
                SetPriorityClass(_handle, _oldPriority);
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr hProcess);
    }
}
