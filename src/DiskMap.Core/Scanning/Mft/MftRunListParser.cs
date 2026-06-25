using System.Buffers.Binary;

namespace DiskMap.Core.Scanning.Mft;

/// <summary>One physical extent of the $MFT on disk: ClusterCount clusters starting at StartLcn.</summary>
public readonly record struct DataRun(long StartLcn, long ClusterCount, bool IsSparse);

/// <summary>
/// Decodes NTFS non-resident data runs. Needed because a volume's $MFT is frequently
/// fragmented in practice (verified on a real, heavily-used drive) — assuming it is one
/// contiguous extent silently under-reads most of the file table instead of failing loudly.
/// </summary>
public static class MftRunListParser
{
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;

    /// <summary>
    /// Extracts the physical cluster runs of the volume's own $MFT from its bootstrap record
    /// (MFT record 0, which always describes the $MFT file itself and is always reachable at
    /// the very start of the MFT's first extent).
    /// </summary>
    public static List<DataRun> GetMftDataRuns(byte[] mftRecord0)
    {
        if (mftRecord0.Length < 0x38)
            throw new IOException("$MFT bootstrap record is too short.");

        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(mftRecord0.AsSpan(0x14, 2));
        int offset = firstAttributeOffset;

        while (offset + 16 <= mftRecord0.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(mftRecord0.AsSpan(offset, 4));
            if (type == AttrEnd) break;

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(mftRecord0.AsSpan(offset + 4, 4));
            if (length < 16 || offset + length > mftRecord0.Length) break;

            byte nonResident = mftRecord0[offset + 8];
            byte nameLength = mftRecord0[offset + 9];

            if (type == AttrData && nameLength == 0 && nonResident == 1)
            {
                ushort dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(mftRecord0.AsSpan(offset + 0x20, 2));
                return ParseRunList(mftRecord0, offset + dataRunsOffset, offset + (int)length);
            }

            offset += (int)length;
        }

        throw new IOException("Could not locate the $MFT's own non-resident $DATA attribute in its bootstrap record.");
    }

    /// <summary>Decodes a raw NTFS data-run byte sequence in [pos, end) into physical extents.</summary>
    public static List<DataRun> ParseRunList(byte[] buffer, int pos, int end)
    {
        var runs = new List<DataRun>();
        long currentLcn = 0;

        while (pos < end)
        {
            byte header = buffer[pos];
            if (header == 0) break; // end-of-runlist marker
            pos++;

            int lengthSize = header & 0x0F;
            int offsetSize = (header >> 4) & 0x0F;
            if (lengthSize == 0 || pos + lengthSize + offsetSize > end) break;

            long clusterCount = ReadUnsigned(buffer, pos, lengthSize);
            pos += lengthSize;

            if (offsetSize == 0)
            {
                // Sparse run: no physical allocation backs these clusters.
                runs.Add(new DataRun(-1, clusterCount, IsSparse: true));
                continue;
            }

            long lcnDelta = ReadSigned(buffer, pos, offsetSize);
            pos += offsetSize;
            currentLcn += lcnDelta;
            runs.Add(new DataRun(currentLcn, clusterCount, IsSparse: false));
        }

        return runs;
    }

    private static long ReadUnsigned(byte[] buffer, int offset, int size)
    {
        long value = 0;
        for (int i = 0; i < size; i++)
            value |= (long)buffer[offset + i] << (8 * i);
        return value;
    }

    private static long ReadSigned(byte[] buffer, int offset, int size)
    {
        long value = ReadUnsigned(buffer, offset, size);
        if (size < 8 && (buffer[offset + size - 1] & 0x80) != 0)
            value |= -1L << (8 * size); // sign-extend
        return value;
    }
}
