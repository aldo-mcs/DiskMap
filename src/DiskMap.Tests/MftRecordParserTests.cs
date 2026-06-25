using System.Buffers.Binary;
using System.Text;
using DiskMap.Core.Scanning.Mft;

namespace DiskMap.Tests;

public class MftRecordParserTests
{
    [Fact]
    public void ApplyFixups_RestoresOriginalSectorEndBytes()
    {
        var record = new byte[1024];
        WriteAscii(record, 0x00, "FILE");
        WriteU16(record, 0x04, 0x30); // UsaOffset
        WriteU16(record, 0x06, 3);    // UsaCount: 1 USN + 2 sector entries

        WriteU16(record, 0x30, 0x0001); // USN marker
        WriteU16(record, 0x32, 0xAAAA); // real bytes for sector 1 end
        WriteU16(record, 0x34, 0xBBBB); // real bytes for sector 2 end

        // Simulate what's actually on disk: USN marker stamped at every sector boundary.
        WriteU16(record, 510, 0x0001);
        WriteU16(record, 1022, 0x0001);

        MftRecordParser.ApplyFixups(record, bytesPerSector: 512);

        Assert.Equal(0xAAAA, ReadU16(record, 510));
        Assert.Equal(0xBBBB, ReadU16(record, 1022));
    }

    [Fact]
    public void TryParse_ExtractsNameParentSizeAndTimestamp()
    {
        var lastWrite = new DateTime(2026, 3, 5, 12, 30, 0, DateTimeKind.Utc);
        byte[] record = BuildFileRecord(isDirectory: false, parentIndex: 5, name: "hello.txt",
            lastWriteUtc: lastWrite, dataSize: 12345, includeData: true);

        bool ok = MftRecordParser.TryParse(record, recordIndex: 42, out var parsed);

        Assert.True(ok);
        Assert.Equal(42, parsed.RecordIndex);
        Assert.Equal(5, parsed.ParentIndex);
        Assert.Equal("hello.txt", parsed.Name);
        Assert.False(parsed.IsDirectory);
        Assert.Equal(12345, parsed.Size);
        Assert.Equal(lastWrite, parsed.LastWriteTimeUtc);
    }

    [Fact]
    public void TryParse_DirectoryRecordHasNoDataSize()
    {
        byte[] record = BuildFileRecord(isDirectory: true, parentIndex: 5, name: "Documents",
            lastWriteUtc: DateTime.UtcNow, dataSize: 0, includeData: false);

        bool ok = MftRecordParser.TryParse(record, recordIndex: 99, out var parsed);

        Assert.True(ok);
        Assert.True(parsed.IsDirectory);
        Assert.Equal(0, parsed.Size);
    }

    [Fact]
    public void TryParse_SkipsRecordsNotInUse()
    {
        byte[] record = BuildFileRecord(isDirectory: false, parentIndex: 5, name: "deleted.tmp",
            lastWriteUtc: DateTime.UtcNow, dataSize: 10, includeData: true, inUse: false);

        bool ok = MftRecordParser.TryParse(record, recordIndex: 7, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_SkipsExtensionRecords()
    {
        byte[] record = BuildFileRecord(isDirectory: false, parentIndex: 5, name: "fragmented.bin",
            lastWriteUtc: DateTime.UtcNow, dataSize: 10, includeData: true, baseFileRecord: 123);

        bool ok = MftRecordParser.TryParse(record, recordIndex: 7, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_RejectsBadMagic()
    {
        var record = new byte[1024];
        WriteAscii(record, 0, "BAAD");

        Assert.False(MftRecordParser.TryParse(record, 1, out _));
    }

    [Fact]
    public void TryParse_AttributeListWithoutData_CapturesHintAndKeepsSizeUnresolved()
    {
        // No $DATA attribute present at all (it lives in an extension record per the $ATTRIBUTE_LIST).
        var record = BuildFileRecord(isDirectory: false, parentIndex: 5, name: "huge.vhdx",
            lastWriteUtc: DateTime.UtcNow, dataSize: 0, includeData: false);

        int offset = 0x38 + 0x18 + 48 + 0x18 + (0x42 + "huge.vhdx".Length * 2);
        // $ATTRIBUTE_LIST: one entry for $DATA (type 0x80), unnamed, StartingVCN=0, pointing at record 999.
        const int entryLength = 26;
        WriteAttributeHeader(record, offset, type: 0x20, contentLength: entryLength, contentOffset: 0x18);
        int listStart = offset + 0x18;
        WriteU32(record, listStart + 0, 0x80);           // entry AttributeType
        WriteU16(record, listStart + 4, entryLength);    // entry length
        record[listStart + 6] = 0;                       // entry name length
        WriteI64(record, listStart + 8, 0);              // StartingVCN = 0
        WriteU64(record, listStart + 16, 999);           // BaseFileReference -> record 999
        offset += 0x18 + entryLength;
        WriteU32(record, offset, 0xFFFFFFFF); // end marker

        bool ok = MftRecordParser.TryParse(record, recordIndex: 5000, out var parsed);

        Assert.True(ok);
        Assert.Equal(999, parsed.DataAttributeRecordHint);
        Assert.Equal(0, parsed.Size); // not yet resolved; caller patches this in from record 999
    }

    [Fact]
    public void TryParse_ExtractsHardLinkCountAndAlternateStreamCount()
    {
        byte[] record = BuildFileRecord(isDirectory: false, parentIndex: 5, name: "downloaded.exe",
            lastWriteUtc: DateTime.UtcNow, dataSize: 100, includeData: true, hardLinkCount: 3,
            alternateStreamNames: ["Zone.Identifier"]);

        bool ok = MftRecordParser.TryParse(record, recordIndex: 12, out var parsed);

        Assert.True(ok);
        Assert.Equal(3, parsed.HardLinkCount);
        Assert.Equal(1, parsed.AlternateStreamCount);
    }

    [Fact]
    public void TryParse_DefaultsToOneLinkAndNoAlternateStreams()
    {
        byte[] record = BuildFileRecord(isDirectory: false, parentIndex: 5, name: "plain.txt",
            lastWriteUtc: DateTime.UtcNow, dataSize: 10, includeData: true, hardLinkCount: 1);

        bool ok = MftRecordParser.TryParse(record, recordIndex: 13, out var parsed);

        Assert.True(ok);
        Assert.Equal(1, parsed.HardLinkCount);
        Assert.Equal(0, parsed.AlternateStreamCount);
    }

    [Fact]
    public void TryReadDataAttributeSize_ReadsNonResidentRealAndAllocatedSize()
    {
        var record = BuildFileRecord(isDirectory: false, parentIndex: 5, name: "extension.bin",
            lastWriteUtc: DateTime.UtcNow, dataSize: 0, includeData: false);

        int offset = 0x38 + 0x18 + 48 + 0x18 + (0x42 + "extension.bin".Length * 2);
        const int totalAttributeLength = 0x40; // common header (0x10) + non-resident fields through InitializedSize (0x30)
        WriteU32(record, offset + 0x00, 0x80); // type $DATA
        WriteU32(record, offset + 0x04, totalAttributeLength);
        record[offset + 0x08] = 1; // non-resident
        record[offset + 0x09] = 0; // unnamed
        WriteI64(record, offset + 0x28, 90_112); // AllocatedSize
        WriteI64(record, offset + 0x30, 87_654); // RealSize
        offset += totalAttributeLength;
        WriteU32(record, offset, 0xFFFFFFFF);

        bool ok = MftRecordParser.TryReadDataAttributeSize(record, out long size, out long allocated);

        Assert.True(ok);
        Assert.Equal(87_654, size);
        Assert.Equal(90_112, allocated);
    }

    // ---- synthetic record builder ----

    private static byte[] BuildFileRecord(
        bool isDirectory, long parentIndex, string name, DateTime lastWriteUtc, long dataSize,
        bool includeData, bool inUse = true, ulong baseFileRecord = 0, int hardLinkCount = 1,
        IReadOnlyList<string>? alternateStreamNames = null)
    {
        var record = new byte[1024];
        WriteAscii(record, 0x00, "FILE");
        WriteU16(record, 0x04, 0); // UsaOffset (irrelevant to TryParse)
        WriteU16(record, 0x06, 0); // UsaCount

        ushort flags = 0;
        if (inUse) flags |= 0x0001;
        if (isDirectory) flags |= 0x0002;
        WriteU16(record, 0x16, flags);
        WriteU16(record, 0x12, hardLinkCount);

        const ushort firstAttributeOffset = 0x38;
        WriteU16(record, 0x14, firstAttributeOffset);
        WriteU64(record, 0x20, baseFileRecord);

        int offset = firstAttributeOffset;

        // $STANDARD_INFORMATION
        const int siContentLen = 48;
        WriteAttributeHeader(record, offset, type: 0x10, contentLength: siContentLen, contentOffset: 0x18);
        WriteI64(record, offset + 0x18 + 8, lastWriteUtc == DateTime.MinValue ? 0 : lastWriteUtc.ToFileTimeUtc());
        offset += 0x18 + siContentLen;

        // $FILE_NAME
        int nameBytes = name.Length * 2;
        int fnContentLen = 0x42 + nameBytes;
        WriteAttributeHeader(record, offset, type: 0x30, contentLength: fnContentLen, contentOffset: 0x18);
        int fnContent = offset + 0x18;
        WriteU64(record, fnContent, ((ulong)1 << 48) | (ulong)parentIndex); // parent ref, sequence number 1
        record[fnContent + 0x40] = (byte)name.Length;
        record[fnContent + 0x41] = 1; // Win32 namespace
        Encoding.Unicode.GetBytes(name).CopyTo(record, fnContent + 0x42);
        offset += 0x18 + fnContentLen;

        // $DATA (unnamed)
        if (includeData)
        {
            const int dummyContentLen = 16;
            WriteAttributeHeader(record, offset, type: 0x80, contentLength: dummyContentLen, contentOffset: 0x18);
            WriteU32(record, offset + 0x10, (uint)dataSize); // override contentLength field with our test size
            offset += 0x18 + dummyContentLen;
        }

        // $DATA (named — alternate data streams, e.g. Zone.Identifier)
        if (alternateStreamNames is not null)
        {
            foreach (var streamName in alternateStreamNames)
            {
                int streamNameBytes = streamName.Length * 2;
                const int contentLen = 16;
                int totalLen = 0x18 + streamNameBytes + contentLen;
                WriteU32(record, offset + 0x00, 0x80);
                WriteU32(record, offset + 0x04, (uint)totalLen);
                record[offset + 0x08] = 0; // resident
                record[offset + 0x09] = (byte)streamName.Length; // name length
                WriteU16(record, offset + 0x0A, 0x18); // name offset
                WriteU16(record, offset + 0x14, (ushort)(0x18 + streamNameBytes)); // content offset
                Encoding.Unicode.GetBytes(streamName).CopyTo(record, offset + 0x18);
                offset += totalLen;
            }
        }

        WriteU32(record, offset, 0xFFFFFFFF); // end-of-attributes marker
        return record;
    }

    private static void WriteAttributeHeader(byte[] record, int offset, uint type, int contentLength, ushort contentOffset)
    {
        WriteU32(record, offset + 0x00, type);
        WriteU32(record, offset + 0x04, (uint)(0x18 + contentLength)); // total attribute length
        record[offset + 0x08] = 0; // resident
        record[offset + 0x09] = 0; // attribute name length (unnamed)
        WriteU16(record, offset + 0x0A, 0);
        WriteU16(record, offset + 0x0C, 0);
        WriteU16(record, offset + 0x0E, 0);
        WriteU32(record, offset + 0x10, (uint)contentLength);
        WriteU16(record, offset + 0x14, contentOffset);
    }

    private static void WriteAscii(byte[] buf, int offset, string s) => Encoding.ASCII.GetBytes(s).CopyTo(buf, offset);
    private static void WriteU16(byte[] buf, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset, 2), (ushort)value);
    private static void WriteU32(byte[] buf, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset, 4), value);
    private static void WriteU64(byte[] buf, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(offset, 8), value);
    private static void WriteI64(byte[] buf, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(offset, 8), value);
    private static ushort ReadU16(byte[] buf, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset, 2));
}
