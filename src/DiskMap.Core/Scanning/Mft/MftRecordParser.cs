using System.Buffers.Binary;
using System.Text;

namespace DiskMap.Core.Scanning.Mft;

/// <summary>One parsed NTFS FILE record, reduced to what the tree builder needs.</summary>
public sealed record ParsedMftRecord(
    long RecordIndex,
    long ParentIndex,
    string Name,
    bool IsDirectory,
    long Size,
    long AllocatedSize,
    DateTime LastWriteTimeUtc,
    DateTime CreatedUtc = default,
    uint FileAttributes = 0,
    long? DataAttributeRecordHint = null,
    int HardLinkCount = 0,
    int AlternateStreamCount = 0);

/// <summary>
/// Parses raw NTFS FILE records (1024-byte segments, typically). Pure byte-buffer logic with
/// no disk/OS dependency, so it is fully unit-testable. Deliberately conservative: any record
/// that doesn't pass basic structural sanity checks is skipped rather than guessed at.
/// </summary>
public static class MftRecordParser
{
    private const uint RecordMagic = 0x454C4946; // "FILE" little-endian
    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;
    private const uint AttrStandardInformation = 0x10;
    private const uint AttrAttributeList = 0x20;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;

    /// <summary>
    /// Applies the NTFS Update Sequence Array fixup in place: the last two bytes of every sector
    /// in the record are a "USN" placeholder that must be replaced with the real bytes stored in
    /// the array, or the record's tail bytes will be corrupt. Mutates <paramref name="record"/>.
    /// </summary>
    public static void ApplyFixups(byte[] record, int bytesPerSector)
    {
        if (record.Length < 8 || bytesPerSector <= 0) return;

        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x04, 2));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x06, 2));
        if (usaOffset == 0 || usaCount == 0) return;

        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * bytesPerSector;
            int fixupEntryOffset = usaOffset + i * 2;
            if (sectorEnd > record.Length || sectorEnd < 2 || fixupEntryOffset + 2 > record.Length) break;

            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(fixupEntryOffset, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd - 2, 2), original);
        }
    }

    /// <summary>
    /// Parses a single (already fixed-up) FILE record. <paramref name="recordIndex"/> is the
    /// record's position in the MFT (byteOffset / bytesPerFileRecordSegment), since that is the
    /// authoritative, always-correct way to know a record's own index.
    /// </summary>
    public static bool TryParse(byte[] record, long recordIndex, out ParsedMftRecord parsed)
    {
        parsed = null!;
        if (record.Length < 0x38) return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x00, 4));
        if (magic != RecordMagic) return false;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x16, 2));
        if ((flags & FlagInUse) == 0) return false; // deleted / free slot

        ulong baseFileRecord = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(0x20, 8));
        if ((baseFileRecord & 0x0000FFFFFFFFFFFF) != 0) return false; // extension record, not a primary file

        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14, 2));
        if (firstAttributeOffset >= record.Length) return false;

        bool isDirectory = (flags & FlagDirectory) != 0;
        int hardLinkCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x12, 2));

        long parentIndex = -1;
        string? name = null;
        int namePriority = -1; // higher wins: Win32/Win32&DOS=2, POSIX=1, DOS-only=0
        DateTime lastWrite = DateTime.MinValue;
        DateTime created = DateTime.MinValue;
        uint fileAttributes = 0;
        long size = 0;
        long allocatedSize = 0;
        bool sawUnnamedData = false;
        long? dataAttributeHint = null;
        int alternateStreamCount = 0;

        int offset = firstAttributeOffset;
        while (offset + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset, 4));
            if (type == AttrEnd) break;

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4, 4));
            if (length < 16 || offset + length > record.Length) break; // malformed; stop walking this record

            byte nonResident = record[offset + 8];
            byte nameLength = record[offset + 9];

            if (type == AttrAttributeList && nonResident == 0)
            {
                // A heavily fragmented or multi-stream file can push its $DATA attribute header
                // into a different (extension) MFT record. The attribute list tells us which one.
                ushort contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 0x14, 2));
                uint contentLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 0x10, 4));
                int listStart = offset + contentOffset;
                int listEnd = (int)Math.Min(record.Length, listStart + contentLength);
                int p = listStart;
                while (p + 26 <= listEnd)
                {
                    uint entryType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(p, 4));
                    ushort entryLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(p + 4, 2));
                    if (entryLength < 26 || p + entryLength > listEnd) break;

                    byte entryNameLength = record[p + 6];
                    long startingVcn = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(p + 8, 8));
                    ulong entryBaseRef = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(p + 16, 8));

                    if (entryType == AttrData && entryNameLength == 0 && startingVcn == 0)
                        dataAttributeHint = (long)(entryBaseRef & 0x0000FFFFFFFFFFFF);

                    p += entryLength;
                }
            }
            else if (type == AttrStandardInformation && nonResident == 0)
            {
                ushort contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 0x14, 2));
                int contentStart = offset + contentOffset;
                if (contentStart + 16 <= record.Length)
                {
                    long createdTime = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(contentStart, 8));
                    long fileTime = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(contentStart + 8, 8));
                    created = FileTimeToUtc(createdTime);
                    lastWrite = FileTimeToUtc(fileTime);
                }
                if (contentStart + 36 <= record.Length)
                    fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(contentStart + 32, 4));
            }
            else if (type == AttrFileName && nonResident == 0)
            {
                ushort contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 0x14, 2));
                int c = offset + contentOffset;
                if (c + 0x42 <= record.Length)
                {
                    ulong parentRef = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(c, 8));
                    byte fileNameLength = record[c + 0x40];
                    byte ns = record[c + 0x41];
                    int nameBytes = fileNameLength * 2;
                    if (fileNameLength is > 0 and <= 255 && c + 0x42 + nameBytes <= record.Length)
                    {
                        int priority = ns switch { 1 or 3 => 2, 0 => 1, _ => 0 };
                        if (priority > namePriority)
                        {
                            namePriority = priority;
                            name = Encoding.Unicode.GetString(record, c + 0x42, nameBytes);
                            parentIndex = (long)(parentRef & 0x0000FFFFFFFFFFFF);
                        }
                    }
                }
            }
            else if (type == AttrData && nameLength == 0) // unnamed $DATA = the file's real content stream
            {
                sawUnnamedData = true;
                (size, allocatedSize) = ReadDataAttributeSize(record, offset, nonResident);
            }
            else if (type == AttrData && nameLength > 0)
            {
                // A named stream alongside the primary content — e.g. Zone.Identifier, the
                // hidden marker Windows attaches to downloaded files. Invisible in Explorer.
                alternateStreamCount++;
            }

            offset += (int)length;
        }

        if (name is null) return false; // no usable $FILE_NAME attribute found; can't place this in the tree
        if (!isDirectory && !sawUnnamedData && dataAttributeHint is null) { size = 0; allocatedSize = 0; }

        parsed = new ParsedMftRecord(recordIndex, parentIndex, name, isDirectory, size, allocatedSize, lastWrite,
            created, fileAttributes, dataAttributeHint, hardLinkCount, alternateStreamCount);
        return true;
    }

    /// <summary>
    /// Reads just the unnamed $DATA attribute's size from an already fixed-up record, for
    /// resolving a <see cref="ParsedMftRecord.DataAttributeRecordHint"/> against the extension
    /// record that actually holds it.
    /// </summary>
    public static bool TryReadDataAttributeSize(byte[] record, out long size, out long allocatedSize)
    {
        size = 0;
        allocatedSize = 0;
        if (record.Length < 0x38) return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x00, 4));
        if (magic != RecordMagic) return false;

        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14, 2));
        if (firstAttributeOffset >= record.Length) return false;

        int offset = firstAttributeOffset;
        while (offset + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset, 4));
            if (type == AttrEnd) break;

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4, 4));
            if (length < 16 || offset + length > record.Length) break;

            byte nonResident = record[offset + 8];
            byte nameLength = record[offset + 9];

            if (type == AttrData && nameLength == 0)
            {
                (size, allocatedSize) = ReadDataAttributeSize(record, offset, nonResident);
                return true;
            }

            offset += (int)length;
        }
        return false;
    }

    private static (long Size, long AllocatedSize) ReadDataAttributeSize(byte[] record, int offset, byte nonResident)
    {
        if (nonResident == 0)
        {
            uint contentLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 0x10, 4));
            return (contentLength, contentLength); // resident data costs no extra clusters on disk
        }
        if (offset + 0x38 <= record.Length)
        {
            long allocated = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(offset + 0x28, 8));
            long real = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(offset + 0x30, 8));
            return (Math.Max(0, real), Math.Max(0, allocated));
        }
        return (0, 0);
    }

    private static DateTime FileTimeToUtc(long fileTime)
    {
        if (fileTime <= 0) return DateTime.MinValue;
        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }
}
