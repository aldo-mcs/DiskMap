using DiskMap.Core.Cleanup;

namespace DiskMap.Tests;

public class CleanupTests
{
    [Fact]
    public void Clean_DeletesFilesAndReportsBytesFreed_KeepsRootFolder()
    {
        var root = Directory.CreateTempSubdirectory("diskmap-cleanup-");
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, "a.tmp"), new byte[100]);
            var sub = root.CreateSubdirectory("sub");
            File.WriteAllBytes(Path.Combine(sub.FullName, "b.tmp"), new byte[250]);

            var category = new CleanupCategoryResult(
                CleanupCategoryKind.UserTemp, "Test Category", "desc", 350, 2, [root.FullName]);

            var result = CleanupCleaner.Clean(category);

            Assert.Equal(350, result.BytesFreed);
            Assert.Equal(2, result.FilesDeleted);
            Assert.Equal(0, result.FailedCount);
            Assert.True(Directory.Exists(root.FullName)); // root itself survives
            Assert.False(Directory.Exists(sub.FullName));  // now-empty subdirectory was removed
            Assert.Empty(Directory.EnumerateFileSystemEntries(root.FullName));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Clean_FilePathCategory_DeletesIndividualFiles()
    {
        var root = Directory.CreateTempSubdirectory("diskmap-cleanup-files-");
        try
        {
            string file1 = Path.Combine(root.FullName, "thumbcache_1.db");
            string file2 = Path.Combine(root.FullName, "thumbcache_2.db");
            File.WriteAllBytes(file1, new byte[64]);
            File.WriteAllBytes(file2, new byte[36]);

            var category = new CleanupCategoryResult(
                CleanupCategoryKind.ThumbnailCache, "Thumbnail Cache", "desc", 100, 2, [file1, file2]);

            var result = CleanupCleaner.Clean(category);

            Assert.Equal(100, result.BytesFreed);
            Assert.Equal(2, result.FilesDeleted);
            Assert.False(File.Exists(file1));
            Assert.False(File.Exists(file2));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Clean_MissingPaths_ReturnsZeroWithoutThrowing()
    {
        var category = new CleanupCategoryResult(
            CleanupCategoryKind.WindowsTemp, "Test", "desc", 0, 0,
            [Path.Combine(Path.GetTempPath(), $"diskmap-does-not-exist-{Guid.NewGuid():N}")]);

        var result = CleanupCleaner.Clean(category);

        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void Scan_ReturnsAllExpectedCategories()
    {
        var results = CleanupScanner.Scan();

        Assert.Equal(7, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.TotalSize >= 0);
            Assert.True(r.FileCount >= 0);
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
        });
        Assert.Contains(results, r => r.Kind == CleanupCategoryKind.RecycleBin);
        Assert.Contains(results, r => r.Kind == CleanupCategoryKind.WindowsTemp);
    }
}
