using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using System.Buffers.Binary;

namespace CopperCPX.Tests;

public class CorruptionMatrixTests
{
    private static void Seal(Span<byte> page)
    {
        uint crc = uint.MaxValue;
        for (int n = 0; n < page.Length; n++)
        {
            crc ^= n is >= 8 and < 12 ? 0u : page[n];
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0x82f63b78;
        }
        BinaryPrimitives.WriteUInt32BigEndian(page[8..], ~crc);
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    [InlineData(5)][InlineData(6)][InlineData(7)][InlineData(8)][InlineData(9)]
    public void ValidChecksumStructuralDamageFailsWithinABoundedReadOnlyTraversal(int damage)
    {
        var disk = EngineTests.Fresh(512); var v = new Cpx4Volume<Disk>(disk, new byte[36864]);
        for (int n = 0; n < 8; n++) v.Create(System.Text.Encoding.ASCII.GetBytes($"file{n}"), out _);
        var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram);
        ulong root = IndependentImage.L(disk.Bytes, 65536 + 3 * 1024 + 80);
        Span<byte> page = disk.Bytes.AsSpan((int)root * 512, 1024);
        Assert.True(IndependentImage.S(page, 50) > 0); Assert.True(IndependentImage.S(page, 48) > 1);
        switch (damage)
        {
            case 0: BinaryPrimitives.WriteUInt16BigEndian(page[48..], ushort.MaxValue); break;
            case 1: BinaryPrimitives.WriteUInt16BigEndian(page[52..], 1); break;
            case 2: BinaryPrimitives.WriteUInt16BigEndian(page[50..], 8); break;
            case 3: BinaryPrimitives.WriteUInt64BigEndian(page[40..], root); break;
            case 4: BinaryPrimitives.WriteUInt64BigEndian(page[72..], root); break; // cycle
            case 5: page.Slice(64, 8).CopyTo(page[80..]); break; // duplicate fence
            case 6: BinaryPrimitives.WriteUInt64BigEndian(page[72..], root + 1); break;
            case 7: BinaryPrimitives.WriteUInt64BigEndian(page[24..], 42); break;
            case 8: BinaryPrimitives.WriteUInt16BigEndian(page[48..], 0); BinaryPrimitives.WriteUInt16BigEndian(page[52..], 0); break;
            case 9: BinaryPrimitives.WriteUInt64BigEndian(page[32..], ulong.MaxValue); break;
        }
        Seal(page); Assert.Equal(CpxError.None, e.BeginOperation(CpxOperation.ReadObject, Wide.From(1)));
        StepResult result = StepResult.Pending; int reads = 0;
        for (int step = 0; step < 100; step++)
        {
            result = e.Step(); if (result is StepResult.Complete or StepResult.Failed) break;
            if (result != StepResult.NeedIo) continue;
            var io = e.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector.Low, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); e.CompleteIo(io.Token, io.Bytes, true); reads++;
        }
        Assert.Equal(StepResult.Failed, result); Assert.Equal(CpxError.Corrupt, e.Error); Assert.InRange(reads, 1, 10);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void AnchorFallbackSucceedsOnlyWhenAnIntactAnchorRemains(bool both)
    {
        var disk = EngineTests.Fresh(512); disk.Bytes[65536] ^= 1;
        if (both) disk.Bytes[^1024] ^= 1;
        var ram = new Ram(new byte[100000]); var e = new CpxEngine<Ram>(ram, 32);
        e.Initialize(512, 512, Wide.From(8192), 1, true); e.BeginOperation(CpxOperation.Mount);
        StepResult result = StepResult.Pending;
        for (int n = 0; n < 1000; n++)
        {
            result = e.Step(); if (result is StepResult.Complete or StepResult.Failed) break;
            if (result != StepResult.NeedIo) continue;
            var io = e.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector.Low, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); e.CompleteIo(io.Token, io.Bytes, true);
        }
        Assert.Equal(both ? StepResult.Failed : StepResult.Complete, result);
        Assert.Equal(both ? CpxError.Corrupt : CpxError.None, e.Error);
    }
}
