namespace DiskMap.Core.Snapshots;

public sealed record DirectoryDelta(string Path, long OldSize, long NewSize)
{
    public long Delta => NewSize - OldSize;
}

public static class SnapshotDiff
{
    /// <summary>
    /// Compares two snapshots of the same root by directory path and returns the directories
    /// with the largest absolute size change, biggest first. New directories count as old=0,
    /// removed directories as new=0.
    /// </summary>
    public static List<DirectoryDelta> Compare(
        Dictionary<string, long> older,
        Dictionary<string, long> newer,
        int maxResults = 50)
    {
        var paths = new HashSet<string>(older.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(newer.Keys);

        var deltas = new List<DirectoryDelta>(paths.Count);
        foreach (var path in paths)
        {
            older.TryGetValue(path, out long oldSize);
            newer.TryGetValue(path, out long newSize);
            if (oldSize == newSize) continue;
            deltas.Add(new DirectoryDelta(path, oldSize, newSize));
        }

        deltas.Sort(static (a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        return deltas.Count > maxResults ? deltas.GetRange(0, maxResults) : deltas;
    }
}
