using System.IO.Hashing;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Duplicates;

public sealed class DuplicateGroup
{
    public required long Size { get; init; }
    public required List<string> Paths { get; init; }
    public long WastedBytes => Size * (Paths.Count - 1);
}

public readonly record struct DuplicateProgress(int FilesHashed, int TotalCandidates);

/// <summary>
/// Finds duplicate files. Two-stage: group by exact size (free, stat-only), then for
/// candidate groups hash a short prefix to cull, then full-hash survivors with the fast
/// non-cryptographic XxHash3. Suitable for dedup, not for security.
/// </summary>
public sealed class DuplicateFinder
{
    private const int PrefixBytes = 16 * 1024;
    private const int BufferBytes = 1 * 1024 * 1024;

    public List<DuplicateGroup> Find(
        FileSystemNode root,
        long minimumSize = 1,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Stage 1: group by size.
        var bySize = new Dictionary<long, List<string>>();
        foreach (var file in root.EnumerateFiles())
        {
            if (file.Size < minimumSize) continue;
            if (!bySize.TryGetValue(file.Size, out var list))
                bySize[file.Size] = list = [];
            list.Add(file.Path);
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        int totalCandidates = candidates.Sum(kv => kv.Value.Count);
        int hashed = 0;

        var groups = new List<DuplicateGroup>();

        foreach (var (size, paths) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Stage 2a: cull by prefix hash.
            var byPrefix = new Dictionary<ulong, List<string>>();
            foreach (var path in paths)
            {
                ulong prefix = HashFile(path, PrefixBytes, cancellationToken);
                progress?.Report(new DuplicateProgress(++hashed, totalCandidates));
                if (!byPrefix.TryGetValue(prefix, out var pl))
                    byPrefix[prefix] = pl = [];
                pl.Add(path);
            }

            // Stage 2b: full hash survivors.
            foreach (var prefixGroup in byPrefix.Values.Where(g => g.Count > 1))
            {
                var byFull = new Dictionary<ulong, List<string>>();
                foreach (var path in prefixGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ulong full = HashFile(path, long.MaxValue, cancellationToken);
                    if (!byFull.TryGetValue(full, out var fl))
                        byFull[full] = fl = [];
                    fl.Add(path);
                }

                foreach (var confirmed in byFull.Values.Where(g => g.Count > 1))
                    groups.Add(new DuplicateGroup { Size = size, Paths = confirmed });
            }
        }

        groups.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
        return groups;
    }

    private static ulong HashFile(string path, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var hasher = new XxHash3();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan);
            byte[] buffer = new byte[BufferBytes];
            long remaining = maxBytes;
            int read;
            while (remaining > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, read));
                remaining -= read;
            }
            return hasher.GetCurrentHashAsUInt64();
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
