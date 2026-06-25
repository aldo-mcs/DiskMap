using System.Buffers.Binary;
using DiskMap.Core.Scanning.Mft;

namespace DiskMap.Tests;

public class MftRunListParserTests
{
    [Fact]
    public void ParseRunList_SingleRun()
    {
        byte[] buf = [0x11, 0x05, 0x64, 0x00]; // lengthSize=1,offsetSize=1; length=5; offset=+100
        var runs = MftRunListParser.ParseRunList(buf, 0, buf.Length);

        var run = Assert.Single(runs);
        Assert.Equal(100, run.StartLcn);
        Assert.Equal(5, run.ClusterCount);
        Assert.False(run.IsSparse);
    }

    [Fact]
    public void ParseRunList_MultipleRuns_OffsetsAreRelativeDeltas()
    {
        // Run 1: length=5, offset=+100 -> LCN 100
        // Run 2: length=3, offset=-20  -> LCN 100 - 20 = 80
        byte[] buf = [0x11, 0x05, 0x64, 0x11, 0x03, 0xEC, 0x00];
        var runs = MftRunListParser.ParseRunList(buf, 0, buf.Length);

        Assert.Equal(2, runs.Count);
        Assert.Equal(100, runs[0].StartLcn);
        Assert.Equal(5, runs[0].ClusterCount);
        Assert.Equal(80, runs[1].StartLcn);
        Assert.Equal(3, runs[1].ClusterCount);
    }

    [Fact]
    public void ParseRunList_TwoByteNegativeOffset_SignExtendsCorrectly()
    {
        // offset = -300 as 16-bit little-endian two's complement
        var offsetBytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(offsetBytes, -300);
        byte[] buf = [0x21, 0x02, offsetBytes[0], offsetBytes[1], 0x00]; // lengthSize=1,offsetSize=2; length=2

        var runs = MftRunListParser.ParseRunList(buf, 0, buf.Length);

        var run = Assert.Single(runs);
        Assert.Equal(-300, run.StartLcn);
        Assert.Equal(2, run.ClusterCount);
    }

    [Fact]
    public void ParseRunList_SparseRun_HasNoOffsetBytes()
    {
        byte[] buf = [0x01, 0x0A, 0x00]; // lengthSize=1, offsetSize=0; length=10
        var runs = MftRunListParser.ParseRunList(buf, 0, buf.Length);

        var run = Assert.Single(runs);
        Assert.True(run.IsSparse);
        Assert.Equal(10, run.ClusterCount);
    }

    [Fact]
    public void ParseRunList_EmptyMarker_ProducesNoRuns()
    {
        byte[] buf = [0x00];
        var runs = MftRunListParser.ParseRunList(buf, 0, buf.Length);
        Assert.Empty(runs);
    }
}
