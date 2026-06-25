using System.Collections.Concurrent;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Thread-safe collector for paths the scan couldn't read (access denied, in use, I/O error). Corruption
/// tolerance means the scan keeps going — this is how the user learns *what* was skipped and how much,
/// rather than the silent swallowing the scanner used to do.
/// </summary>
public sealed class ScanErrorCollector
{
    /// <summary>Cap on the per-path list so a pathologically bad drive can't exhaust memory logging errors.</summary>
    private const int MaxRecordedPaths = 200;

    private int _count;
    private readonly ConcurrentQueue<string> _paths = new();

    /// <summary>Total number of files/directories skipped, including ones beyond <see cref="MaxRecordedPaths"/>.</summary>
    public int Count => _count;

    /// <summary>The first ~200 skipped paths (in encounter order) for display. May be fewer than <see cref="Count"/>.</summary>
    public IReadOnlyCollection<string> Paths => _paths;

    /// <summary>Records a skipped path. Cheap enough to call per-failure from worker threads.</summary>
    public void Record(string path)
    {
        Interlocked.Increment(ref _count);
        if (_paths.Count < MaxRecordedPaths)
            _paths.Enqueue(path);
    }
}
