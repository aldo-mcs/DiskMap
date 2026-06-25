using DiskMap.Core.Duplicates;
using DiskMap.Core.FileTypes;
using DiskMap.Core.Scanning;
using DiskMap.Core.Snapshots;

namespace DiskMap.Tests;

public class CoreTests
{
    [Fact]
    public void Scan_AggregatesSizesCountsAndExtensions()
    {
        var root = Directory.CreateTempSubdirectory("diskmap-scan-");
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, "a.txt"), new byte[100]);
            File.WriteAllBytes(Path.Combine(root.FullName, "b.mp4"), new byte[200]);
            var sub = root.CreateSubdirectory("sub");
            File.WriteAllBytes(Path.Combine(sub.FullName, "c.txt"), new byte[300]);

            var result = new DirectoryScanner().Scan(root.FullName);

            Assert.Equal(600, result.Size);
            Assert.Equal(3, result.FileCount);
            Assert.True(result.AllocatedSize >= result.Size); // cluster rounding

            var stats = ExtensionStatsBuilder.Build(result);
            var txt = stats.Single(s => s.Extension == ".txt");
            Assert.Equal(400, txt.TotalSize);
            Assert.Equal(2, txt.FileCount);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void DuplicateFinder_FindsIdenticalFiles()
    {
        var root = Directory.CreateTempSubdirectory("diskmap-dup-");
        try
        {
            byte[] content = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            File.WriteAllBytes(Path.Combine(root.FullName, "one.bin"), content);
            File.WriteAllBytes(Path.Combine(root.FullName, "two.bin"), content);
            File.WriteAllBytes(Path.Combine(root.FullName, "different.bin"), [9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);

            var tree = new DirectoryScanner().Scan(root.FullName);
            var groups = new DuplicateFinder().Find(tree);

            var group = Assert.Single(groups);
            Assert.Equal(2, group.Paths.Count);
            Assert.Equal(10, group.WastedBytes);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void SnapshotStore_SavesAndDiffsByDirectory()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"diskmap-snap-{Guid.NewGuid():N}.db");
        var root = Directory.CreateTempSubdirectory("diskmap-snaproot-");
        try
        {
            var store = new SnapshotStore(dbPath);

            File.WriteAllBytes(Path.Combine(root.FullName, "x.bin"), new byte[100]);
            var first = new DirectoryScanner().Scan(root.FullName);
            long id1 = store.Save(first);

            File.WriteAllBytes(Path.Combine(root.FullName, "y.bin"), new byte[500]);
            var second = new DirectoryScanner().Scan(root.FullName);
            long id2 = store.Save(second);

            Assert.Equal(2, store.List(root.FullName).Count);

            var diff = SnapshotDiff.Compare(store.LoadEntrySizes(id1), store.LoadEntrySizes(id2));
            var rootDelta = diff.Single(d => string.Equals(d.Path, root.FullName, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(500, rootDelta.Delta);
        }
        finally
        {
            root.Delete(recursive: true);
            // Release pooled SQLite connections so the temp DB file can be removed.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
