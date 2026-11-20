using System.Reflection;
using System.Reflection.Emit;
using CopperFS.Prototype;
using static CopperFS.Prototype.Tests.DesignTests;

namespace CopperFS.Prototype.Tests;

public class ScratchBudgetTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact] public void CallerOwnedContinuationsHaveFixedExplicitBudgets()
    {
        int free=System.Runtime.CompilerServices.Unsafe.SizeOf<FreeSearch>();
        int name=System.Runtime.CompilerServices.Unsafe.SizeOf<NameSearch>();
        int ancestry=System.Runtime.CompilerServices.Unsafe.SizeOf<AncestrySearch>();
        Assert.InRange(free,1,64); Assert.InRange(name,1,1152); Assert.InRange(ancestry,1,256);
        output.WriteLine($"Host cursor sizes: free {free}, name {name}, ancestry {ancestry} bytes. Reserve 64/1152/256; native ABI must be measured separately.");
    }
    private sealed class FailedCooperationDevice(byte[] bytes) : MemoryDevice(bytes), IBlockDevice
    {
        internal bool Fail;
        public void Cooperate() { if(Fail) throw new IOException("Injected scheduling callback failure."); }
    }
    [Fact] public void SchedulingFailureUnwindsRecordsAndPoisonsTheMount()
    {
        var d=new FailedCooperationDevice(Fresh().Bytes); var v=new PrototypeVolume<FailedCooperationDevice>(d,Workspace()); d.Fail=true;
        try { v.Create("failure"u8,out _); Assert.Fail("Scheduling failure was ignored."); } catch(IOException) { }
        Assert.Equal(0,v.UsedRecordScratchBytes); d.Fail=false;
        try { v.Create("unsafe"u8,out _); Assert.Fail("Poisoned mount accepted mutation."); } catch(InvalidOperationException) { }
        var recovered=new PrototypeVolume<MemoryDevice>(new MemoryDevice(d.Bytes),Workspace()); recovered.Check();
    }
    [Theory] [InlineData(512)] [InlineData(4096)]
    public void NestedOperationsStayInsideRecordArenaAndPreserveScratchGuards(int allocation)
    {
        var d=Fresh(allocation); int length=PrototypeVolume<MemoryDevice>.RequiredScratchBytes(Math.Max(1024,allocation));
        byte[] guarded=Enumerable.Repeat((byte)0xcd,length+64).ToArray(); var v=new PrototypeVolume<MemoryDevice>(d,guarded.AsSpan(32,length));
        v.Create(1,"sub"u8,true,out ulong sub); v.Put("file"u8,Pattern(40000)); v.TryGetInfo("file"u8,out var info);
        v.Link(info.Id,sub,"link"u8); v.Rename(1,"file"u8,sub,"longer-name"u8); v.Truncate(info.Id,103); v.Delete(sub,"link"u8); v.Delete(sub,"longer-name"u8); v.Delete("sub"u8); v.Check();
        Assert.Equal(0,v.UsedRecordScratchBytes); Assert.InRange(v.PeakRecordScratchBytes,1,4096);
        Assert.All(guarded[..32],b=>Assert.Equal(0xcd,b)); Assert.All(guarded[(length+32)..],b=>Assert.Equal(0xcd,b));
        output.WriteLine($"A={allocation}: peak record arena {v.PeakRecordScratchBytes}/4096 bytes; total scratch {length} bytes.");
    }
    [Fact] public void RecordsNeverUseLocallocAndArithmeticHasNoWideLocalsOrDivision()
    {
        foreach(var method in typeof(PrototypeVolume<MemoryDevice>).GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
        {
            var body=method.GetMethodBody(); if(body==null)continue;
            if(method.Name!="NameHash") Assert.DoesNotContain(Instructions(body), op=>op==OpCodes.Localloc);
        }
        foreach(var method in typeof(WideMath32).GetMethods(BindingFlags.Public|BindingFlags.Static))
        {
            var body=method.GetMethodBody()!; Assert.DoesNotContain(body.LocalVariables,v=>v.LocalType==typeof(ulong)||v.LocalType==typeof(long));
            Assert.DoesNotContain(Instructions(body), op=>op==OpCodes.Div||op==OpCodes.Div_Un||op==OpCodes.Rem||op==OpCodes.Rem_Un||op==OpCodes.Mul||op==OpCodes.Newarr||op==OpCodes.Localloc);
        }
    }
    private static IEnumerable<OpCode> Instructions(MethodBody body)
    {
        var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(c=>unchecked((ushort)c.Value));
        byte[] il=body.GetILAsByteArray()!;
        for(int offset=0;offset<il.Length;)
        {
            ushort code=il[offset++]; if(code==0xfe)code=(ushort)(0xfe00|il[offset++]); var op=codes[code]; yield return op;
            int size=op.OperandType switch { OperandType.InlineNone=>0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1, OperandType.InlineVar=>2,
                OperandType.InlineI8 or OperandType.InlineR=>8, OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,offset), _=>4 }; offset+=size;
        }
    }
}
