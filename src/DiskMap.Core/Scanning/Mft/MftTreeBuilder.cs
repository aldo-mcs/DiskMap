namespace DiskMap.Core.Scanning.Mft;

internal static class MftTreeBuilder
{
    /// <summary>
    /// Builds a <see cref="FileSystemNode"/> tree from flat MFT records via top-down BFS from
    /// the volume root (record 5), so every node's full path is known before it is constructed
    /// (required because <see cref="FileSystemNode.Path"/> is init-only). Records whose parent
    /// chain never reaches the root (deleted/corrupt ancestors, or in the extreme a corrupted
    /// parent cycle) are attached directly under the root rather than silently dropped.
    /// </summary>
    public static (FileSystemNode Root, Dictionary<string, FileSystemNode> PathLookup) Build(
        Dictionary<long, ParsedMftRecord> records, string driveRootPath, long volumeRootIndex)
    {
        var childrenOf = new Dictionary<long, List<long>>();
        foreach (var (index, rec) in records)
        {
            if (index == volumeRootIndex) continue;
            if (!childrenOf.TryGetValue(rec.ParentIndex, out var list))
                childrenOf[rec.ParentIndex] = list = [];
            list.Add(index);
        }

        var pathLookup = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        var nodesByIndex = new Dictionary<long, FileSystemNode>(records.Count);

        var root = new FileSystemNode
        {
            Path = driveRootPath,
            Name = driveRootPath,
            IsDirectory = true,
            LastModified = records.TryGetValue(volumeRootIndex, out var rootRec) ? rootRec.LastWriteTimeUtc : DateTime.MinValue,
            Created = rootRec?.CreatedUtc ?? DateTime.MinValue,
            Attributes = rootRec?.FileAttributes ?? 0,
            MftRecordIndex = volumeRootIndex,
            HardLinkCount = rootRec?.HardLinkCount ?? 0,
            AlternateStreamCount = rootRec?.AlternateStreamCount ?? 0,
        };
        nodesByIndex[volumeRootIndex] = root;
        pathLookup[driveRootPath.TrimEnd('\\')] = root;

        var visited = new HashSet<long> { volumeRootIndex };
        var queue = new Queue<long>();
        queue.Enqueue(volumeRootIndex);

        while (queue.Count > 0)
        {
            long parentIndex = queue.Dequeue();
            var parentNode = nodesByIndex[parentIndex];
            if (!childrenOf.TryGetValue(parentIndex, out var childIndices)) continue;

            foreach (long childIndex in childIndices)
            {
                if (!visited.Add(childIndex)) continue; // guards against any parent cycle

                var rec = records[childIndex];
                string childPath = CombinePath(parentNode.Path, rec.Name);
                var node = new FileSystemNode
                {
                    Path = childPath,
                    Name = rec.Name,
                    IsDirectory = rec.IsDirectory,
                    Size = rec.Size,
                    AllocatedSize = rec.AllocatedSize,
                    FileCount = rec.IsDirectory ? 0 : 1,
                    LastModified = rec.LastWriteTimeUtc,
                    Created = rec.CreatedUtc,
                    Attributes = rec.FileAttributes,
                    MftRecordIndex = childIndex,
                    HardLinkCount = rec.HardLinkCount,
                    AlternateStreamCount = rec.AlternateStreamCount,
                    Parent = parentNode,
                };
                parentNode.Children.Add(node);
                nodesByIndex[childIndex] = node;
                pathLookup[childPath] = node;
                queue.Enqueue(childIndex);
            }
        }

        // Anything never reached from the root (orphaned/corrupt parent chain) still gets shown,
        // attached directly under the root, rather than silently vanishing from the size totals.
        foreach (var (index, rec) in records)
        {
            if (index == volumeRootIndex || visited.Contains(index)) continue;

            string path = CombinePath(root.Path, rec.Name);
            var node = new FileSystemNode
            {
                Path = path,
                Name = rec.Name,
                IsDirectory = rec.IsDirectory,
                Size = rec.Size,
                AllocatedSize = rec.AllocatedSize,
                FileCount = rec.IsDirectory ? 0 : 1,
                LastModified = rec.LastWriteTimeUtc,
                Created = rec.CreatedUtc,
                Attributes = rec.FileAttributes,
                MftRecordIndex = index,
                HardLinkCount = rec.HardLinkCount,
                AlternateStreamCount = rec.AlternateStreamCount,
                Parent = root,
            };
            root.Children.Add(node);
            pathLookup[path] = node;
        }

        Aggregate(root);
        SortDescendingBySize(root);

        return (root, pathLookup);
    }

    private static string CombinePath(string parentPath, string name) =>
        parentPath.EndsWith('\\') ? parentPath + name : parentPath + "\\" + name;

    private static void Aggregate(FileSystemNode node)
    {
        if (!node.IsDirectory || node.Children.Count == 0) return;

        long size = 0, allocated = 0, files = 0;
        foreach (var child in node.Children)
        {
            if (child.IsDirectory) Aggregate(child);
            size += child.Size;
            allocated += child.AllocatedSize;
            files += child.FileCount; // files: always 1; directories: already aggregated above
        }
        node.Size = size;
        node.AllocatedSize = allocated;
        node.FileCount = files;
    }

    private static void SortDescendingBySize(FileSystemNode node)
    {
        if (!node.IsDirectory || node.Children.Count == 0) return;
        node.Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        foreach (var child in node.Children)
            SortDescendingBySize(child);
    }
}
