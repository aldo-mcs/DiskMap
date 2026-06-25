using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Crash-safe scanning support. During a recursive scan, completed-directory aggregates are written to a
/// JSON checkpoint periodically; if the process is killed or the scan is cancelled, the next scan of the same
/// root can load the checkpoint and skip directories already finished rather than starting over.
///
/// MFT scans are excluded — they finish in seconds, so resume would just add I/O cost for no benefit.
/// </summary>
public sealed class ScanCheckpoint : IDisposable
{
    private static readonly string CheckpointDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap", "checkpoints");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, DirEntry> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(2);

    /// <summary>Completed-directory aggregates seen so far: path → (size, allocated, fileCount).</summary>
    public sealed record DirEntry(long Size, long Allocated, long Files);

    /// <summary>One serialized completed directory.</summary>
    public sealed class CheckpointEntry
    {
        public string Path { get; set; } = "";
        public DirEntry Entry { get; set; } = new(0, 0, 0);
    }

    private sealed class CheckpointFile
    {
        public DateTime WrittenUtc { get; set; }
        public List<CheckpointEntry> Entries { get; set; } = [];
    }

    public ScanCheckpoint(string rootPath)
    {
        Directory.CreateDirectory(CheckpointDir);
        _filePath = Path.Combine(CheckpointDir, Slugify(rootPath) + ".ckpt");
    }

    /// <summary>Records a fully-scanned directory's aggregate. Thread-safe. Does not write to disk until
    /// <see cref="MaybeFlush"/> decides enough time/new entries have accumulated.</summary>
    public void RecordCompleted(string path, long size, long allocated, long files) =>
        _completed[path] = new DirEntry(size, allocated, files);

    /// <summary>If at least <paramref name="interval"/> has passed since the last flush, writes the current
    /// set of completed directories to disk. Called from the scanner's progress path.</summary>
    public void MaybeFlush(TimeSpan? interval = null)
    {
        var now = DateTime.UtcNow;
        var wait = interval ?? WriteInterval;
        if (now - _lastWriteUtc < wait) return;
        lock (_writeLock)
        {
            if (now - _lastWriteUtc < wait) return;
            try
            {
                var file = new CheckpointFile
                {
                    WrittenUtc = now,
                    Entries = _completed.Select(kv => new CheckpointEntry { Path = kv.Key, Entry = kv.Value }).ToList(),
                };
                File.WriteAllText(_filePath, JsonSerializer.Serialize(file, JsonOptions));
                _lastWriteUtc = now;
            }
            catch (IOException) { /* checkpoint is best-effort; scan continues either way */ }
        }
    }

    /// <summary>If a checkpoint exists for this root, loads the previously-completed directory aggregates so
    /// the scan can skip them. Returns null if none or unreadable.</summary>
    public Dictionary<string, DirEntry>? LoadExisting()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var data = JsonSerializer.Deserialize<CheckpointFile>(File.ReadAllText(_filePath), JsonOptions);
            if (data?.Entries is null || data.Entries.Count == 0) return null;
            var dict = new Dictionary<string, DirEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in data.Entries) dict[e.Path] = e.Entry;
            return dict;
        }
        catch { return null; }
    }

    /// <summary>Removes the checkpoint file — call after a scan completes successfully, so a future scan of the
    /// same root starts fresh.</summary>
    public void Clear()
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); }
        catch (IOException) { }
    }

    public void Dispose() => Clear();

    /// <summary>Map a root path to a filesystem-safe checkpoint filename. Hashed so arbitrary paths
    /// (including long ones) produce short, stable names without colliding characters.</summary>
    private static string Slugify(string rootPath)
    {
        // Simple FNV-1a hash → base36. Deterministic, no dependency on System.IO.Hashing.
        ulong hash = 14695981039346656037;
        foreach (char c in rootPath) { hash ^= c; hash *= 1099511628211; }
        Span<char> buf = stackalloc char[16];
        buf[0] = 'h';
        int n = 1;
        Span<char> tmp = stackalloc char[16];
        int len = 0;
        if (hash == 0) tmp[len++] = '0';
        while (hash > 0) { tmp[len++] = ToBase36((int)(hash % 36)); hash /= 36; }
        for (int i = len - 1; i >= 0; i--) buf[n++] = tmp[i];
        return new string(buf[..n]);
    }

    private static char ToBase36(int v) => (char)(v < 10 ? '0' + v : 'a' + (v - 10));
}
