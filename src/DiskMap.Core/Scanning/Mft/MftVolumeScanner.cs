namespace DiskMap.Core.Scanning.Mft;

/// <summary>Raw counters from the most recent $MFT read, surfaced for Advanced Mode so an
/// engineer can see what the fast path actually did rather than just trusting it.</summary>
public sealed record MftScanDiagnostics(
    long RecordsAttempted,
    long RecordsParsed,
    int RunCount,
    long MftLengthBytes,
    double ReadElapsedMs);

/// <summary>
/// Reads a volume's $MFT directly for near-instant whole-drive scans, NTFS-only and
/// administrator-only (raw volume access requires elevation). Always falls back cleanly:
/// any failure is reported via <paramref name="failureReason"/> rather than thrown, so callers
/// can transparently retry with the slower recursive <see cref="DirectoryScanner"/>.
///
/// Follows the $MFT's own data-run list (<see cref="MftRunListParser"/>) rather than assuming
/// it is contiguous on disk — verified necessary: a fragmented $MFT is common on real,
/// long-lived volumes, and reading it as one span silently misses most of the file table
/// instead of failing loudly. ATTRIBUTE_LIST extension records (for files with unusually many
/// attributes) are skipped without losing size accuracy, since size/name metadata lives in the
/// base record regardless.
/// </summary>
public static class MftVolumeScanner
{
    private const long VolumeRootIndex = 5;
    private const int RecordsPerChunk = 8192;

    public static bool TryScan(
        string targetPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        out FileSystemNode? root,
        out string? failureReason,
        out MftScanDiagnostics? diagnostics)
    {
        root = null;
        failureReason = null;
        diagnostics = null;

        string? rootPath = Path.GetPathRoot(targetPath);
        if (string.IsNullOrEmpty(rootPath))
        {
            failureReason = "Could not determine the drive for this path.";
            return false;
        }
        string driveLetter = rootPath.TrimEnd('\\', ':');

        try
        {
            var driveInfo = new DriveInfo(driveLetter);
            if (!string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "Not an NTFS volume.";
                return false;
            }
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }

        try
        {
            var records = ReadAllRecords(driveLetter, progress, cancellationToken, out diagnostics);
            if (records.Count == 0)
            {
                failureReason = "No MFT records could be parsed.";
                return false;
            }

            string driveRootPath = driveLetter + @":\";
            var (fullRoot, pathLookup) = MftTreeBuilder.Build(records, driveRootPath, VolumeRootIndex);

            string normalizedTarget = targetPath.TrimEnd('\\');
            if (string.Equals(normalizedTarget + "\\", driveRootPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedTarget, driveLetter + ":", StringComparison.OrdinalIgnoreCase))
            {
                // Defense in depth: rare structural edge cases (e.g. hard links claimed by more
                // than one parent) can skew individual folders even when the overall read is
                // sound. Cross-check the whole-drive total against the volume's actual used
                // space and refuse to hand back a result that's implausibly far off, rather than
                // risk presenting confidently wrong numbers.
                if (!IsPlausibleAgainstVolumeUsage(driveLetter, fullRoot.AllocatedSize, out failureReason))
                    return false;

                root = fullRoot;
                return true;
            }

            if (pathLookup.TryGetValue(normalizedTarget, out var subtreeRoot))
            {
                subtreeRoot.Parent = null;
                root = subtreeRoot;
                return true;
            }

            failureReason = $"Could not locate '{targetPath}' in the MFT-derived tree.";
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private const double MaxPlausibleRelativeError = 0.10;

    private static bool IsPlausibleAgainstVolumeUsage(string driveLetter, long scannedAllocatedSize, out string? failureReason)
    {
        failureReason = null;
        try
        {
            var driveInfo = new DriveInfo(driveLetter);
            long usedSpace = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
            if (usedSpace <= 0) return true; // can't sanity-check; don't block on it

            double relativeError = Math.Abs(scannedAllocatedSize - usedSpace) / (double)usedSpace;
            if (relativeError > MaxPlausibleRelativeError)
            {
                failureReason = $"MFT-derived total ({scannedAllocatedSize:N0} bytes) diverges too far from actual volume usage ({usedSpace:N0} bytes).";
                return false;
            }
        }
        catch
        {
            // If the check itself fails, don't penalize an otherwise-successful scan for it.
        }
        return true;
    }

    private static Dictionary<long, ParsedMftRecord> ReadAllRecords(
        string driveLetter, IProgress<ScanProgress>? progress, CancellationToken cancellationToken,
        out MftScanDiagnostics diagnostics)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var volumeHandle = NtfsInterop.OpenVolume(driveLetter);
        var volumeData = NtfsInterop.GetVolumeData(volumeHandle);

        int recordSize = checked((int)volumeData.BytesPerFileRecordSegment);
        int sectorSize = checked((int)volumeData.BytesPerSector);
        long bytesPerCluster = volumeData.BytesPerCluster;
        long mftStartOffset = volumeData.MftStartLcn * bytesPerCluster;
        long mftLength = volumeData.MftValidDataLength;
        if (recordSize <= 0 || sectorSize <= 0 || mftLength <= 0 || bytesPerCluster <= 0)
            throw new IOException("Implausible NTFS volume metadata.");

        using var stream = new FileStream(volumeHandle, FileAccess.Read);

        // Bootstrap: MFT record 0 (the $MFT's own file record) always sits at the very start of
        // the MFT's first extent and describes the file table's true physical layout, which is
        // frequently fragmented on real, long-lived volumes — reading it as one contiguous span
        // would silently miss most of the table instead of failing loudly.
        var bootstrap = new byte[recordSize];
        stream.Seek(mftStartOffset, SeekOrigin.Begin);
        if (ReadFully(stream, bootstrap, recordSize) != recordSize)
            throw new IOException("Could not read the $MFT bootstrap record.");
        MftRecordParser.ApplyFixups(bootstrap, sectorSize);
        var runs = MftRunListParser.GetMftDataRuns(bootstrap);
        if (runs.Count == 0 || runs.Exists(r => r.IsSparse))
            throw new IOException("Unexpected $MFT layout (empty or sparse run list).");

        var records = new Dictionary<long, ParsedMftRecord>((int)Math.Min(mftLength / recordSize, 4_000_000));
        byte[] chunk = new byte[(long)RecordsPerChunk * recordSize];
        byte[] recordBuffer = new byte[recordSize];

        long recordIndex = 0;
        long bytesRemainingTotal = mftLength;
        long lastReportedCount = 0;

        foreach (var run in runs)
        {
            if (bytesRemainingTotal <= 0) break;

            long runByteLength = Math.Min(run.ClusterCount * bytesPerCluster, bytesRemainingTotal);
            stream.Seek(run.StartLcn * bytesPerCluster, SeekOrigin.Begin);

            long runBytesRemaining = runByteLength;
            while (runBytesRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(chunk.Length, runBytesRemaining);
                int actuallyRead = ReadFully(stream, chunk, toRead);
                if (actuallyRead <= 0) break;

                int recordsInChunk = actuallyRead / recordSize;
                for (int i = 0; i < recordsInChunk; i++)
                {
                    Buffer.BlockCopy(chunk, i * recordSize, recordBuffer, 0, recordSize);
                    MftRecordParser.ApplyFixups(recordBuffer, sectorSize);
                    if (MftRecordParser.TryParse(recordBuffer, recordIndex, out var parsed))
                        records[recordIndex] = parsed;
                    recordIndex++;
                }

                runBytesRemaining -= actuallyRead;
                bytesRemainingTotal -= actuallyRead;

                if (records.Count - lastReportedCount >= 100_000)
                {
                    lastReportedCount = records.Count;
                    progress?.Report(new ScanProgress(records.Count, 0, $"Reading $MFT ({records.Count:N0} records)..."));
                }
            }
        }

        ResolveRelocatedDataAttributes(records, runs, stream, recordSize, sectorSize, bytesPerCluster);

        diagnostics = new MftScanDiagnostics(recordIndex, records.Count, runs.Count, mftLength, stopwatch.Elapsed.TotalMilliseconds);
        return records;
    }

    /// <summary>
    /// A heavily fragmented or multi-stream file can have its $DATA attribute header pushed into
    /// an extension record elsewhere in the MFT (flagged via <see cref="ParsedMftRecord.DataAttributeRecordHint"/>).
    /// Seeks directly to each hinted record and patches in the real size.
    /// </summary>
    private static void ResolveRelocatedDataAttributes(
        Dictionary<long, ParsedMftRecord> records, List<DataRun> runs, FileStream stream,
        int recordSize, int sectorSize, long bytesPerCluster)
    {
        var hinted = records.Where(kv => kv.Value.DataAttributeRecordHint is not null).ToList();
        if (hinted.Count == 0) return;

        // Map global record index -> physical byte offset, accounting for run fragmentation.
        var runStarts = new List<(long StartIndex, DataRun Run)>(runs.Count);
        long cursor = 0;
        foreach (var run in runs)
        {
            runStarts.Add((cursor, run));
            cursor += run.ClusterCount * bytesPerCluster / recordSize;
        }

        bool TryGetOffset(long index, out long offset)
        {
            for (int i = runStarts.Count - 1; i >= 0; i--)
            {
                if (index >= runStarts[i].StartIndex)
                {
                    offset = runStarts[i].Run.StartLcn * bytesPerCluster + (index - runStarts[i].StartIndex) * recordSize;
                    return true;
                }
            }
            offset = 0;
            return false;
        }

        byte[] buffer = new byte[recordSize];
        foreach (var (key, rec) in hinted)
        {
            long hintIndex = rec.DataAttributeRecordHint!.Value;
            if (!TryGetOffset(hintIndex, out long offset)) continue;

            stream.Seek(offset, SeekOrigin.Begin);
            if (ReadFully(stream, buffer, recordSize) != recordSize) continue;
            MftRecordParser.ApplyFixups(buffer, sectorSize);
            if (MftRecordParser.TryReadDataAttributeSize(buffer, out long size, out long allocatedSize))
                records[key] = rec with { Size = size, AllocatedSize = allocatedSize };
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
