using DiskMap.Core.Scanning;

namespace DiskMap.Tests;

public class DirectoryScannerTests
{
    [Fact]
    public void Scan_AggregatesFileSizesRecursively()
    {
        var tempRoot = Directory.CreateTempSubdirectory("diskmap-test-");
        try
        {
            File.WriteAllBytes(Path.Combine(tempRoot.FullName, "a.txt"), new byte[100]);
            var subdir = tempRoot.CreateSubdirectory("sub");
            File.WriteAllBytes(Path.Combine(subdir.FullName, "b.txt"), new byte[250]);

            var result = new DirectoryScanner().Scan(tempRoot.FullName);

            Assert.Equal(350, result.Size);
            Assert.Equal(2, result.Children.Count); // a.txt + sub
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }
}
