using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class FormatterTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static void Format(IBlockDevice disk, int unit,string? label=null)
    {
        var ram = new Ram(new byte[100000]); var e = new CpxEngine<Ram>(ram, 32);
        byte[] bytes=System.Text.Encoding.Latin1.GetBytes(label??""); bytes.CopyTo(ram.Bytes,90000);
        Assert.Equal(CpxError.None, e.BeginFormat((uint)disk.SectorSize, (uint)unit, new((uint)(disk.SectorCount >> 32), (uint)disk.SectorCount), new(0x1234, 0x5678), 1,90000,(uint)bytes.Length));
        EngineTests.Drive(ref e, ram, disk); Assert.True(e.IsMounted);
    }
    [Theory, MemberData(nameof(GeometryMatrixTests.Geometries), MemberType = typeof(GeometryMatrixTests))]
    public void ResumableFormatterMatchesFrozenReferenceByteForByte(int sector, int unit)
    {
        var native = new MemoryDevice(sector); var reference = new MemoryDevice(sector);
        Format(native, unit); Cpx4Volume<MemoryDevice>.Format(reference, unit, new byte[36864], 0x123400005678UL);
        Assert.Equal(reference.Bytes, native.Bytes); new IndependentImage(native.Bytes); new Cpx4Volume<MemoryDevice>(native, new byte[36864]).Check();
    }
    [Theory][InlineData(512, 4096)][InlineData(4096, 512)][InlineData(512,512)]
    public void ReformatCrashesLeaveEitherTheOldVolumeTheNewVolumeOrNoMountableAnchor(int oldUnit, int newUnit)
    {
        var original = EngineTests.Fresh(oldUnit); var v = new Cpx4Volume<Disk>(original, new byte[36864]); v.Put("old"u8, "original"u8);
        var trace = new CrashDevice(original.Bytes); Format(trace, newUnit,"ÄFreshÿ");
        int old = 0, fresh = 0, invalid = 0;
        for (int cut = 1; cut <= trace.Events.Count; cut++) for (int persistence = 0; persistence < 5; persistence++)
        {
            var crash = new CrashDevice(original.Bytes) { Cut = cut, Persistence = persistence }; Assert.Throws<PowerLoss>(() => Format(crash, newUnit,"ÄFreshÿ"));
            var disk = new MemoryDevice(crash.Durable); bool mounted = false;
            foreach (int unit in new[] { oldUnit, newUnit })
            {
                var ram = new Ram(new byte[100000]); var e = new CpxEngine<Ram>(ram, 32);
                e.Initialize(512, (uint)unit, Wide.From(8192), 1, true); e.BeginOperation(CpxOperation.Mount);
                StepResult result = StepResult.Pending;
                for (int steps = 0; steps < 1000; steps++)
                {
                    result = e.Step(); if (result is StepResult.Complete or StepResult.Failed) break;
                    if (result != StepResult.NeedIo) continue;
                    var io = e.Request; Assert.Equal(IoKind.Read, io.Kind); disk.Read(io.Sector.Low, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); e.CompleteIo(io.Token, io.Bytes, true);
                }
                if (result != StepResult.Complete) { Assert.Equal(StepResult.Failed, result); Assert.Equal(CpxError.Corrupt, e.Error); continue; }
                mounted = true; var decoded = new IndependentImage(disk.Bytes);
                if (decoded.Label=="CopperCPX4") { old++; Assert.Equal(oldUnit,unit); Assert.Equal("original"u8.ToArray(), decoded.Read(decoded.Names[(1, "old")])); }
                else { fresh++; Assert.Equal(newUnit,unit); Assert.Equal("ÄFreshÿ",decoded.Label); Assert.Empty(decoded.Names); }
                new Cpx4Volume<MemoryDevice>(disk, new byte[36864], true).Check(); break;
            }
            if (!mounted) invalid++;
            Assert.Equal(0, disk.Writes); // Detection is never speculative repair.
        }
        Assert.True(old > 0 && fresh > 0 && invalid > 0);
        output.WriteLine($"{trace.Events.Count} cuts × 5 schedules: old={old}, new={fresh}, intentionally unmountable={invalid}.");
    }
}
