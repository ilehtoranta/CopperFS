using System.Text;
using CopperFS.Prototype;
using static CopperFS.Prototype.Layout;
using static CopperFS.Prototype.Tests.DesignTests;

namespace CopperFS.Prototype.Tests;

public class SchedulingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class BudgetDevice(byte[] image) : MemoryDevice(image), IBlockDevice
    {
        internal int SinceYield, Peak, Yields; internal bool Audit;
        private void Request() { if (Audit) { SinceYield++; Peak = Math.Max(Peak, SinceYield); Assert.True(SinceYield <= 4, "Core exceeded four I/O requests without cooperating."); } }
        public override void Read(ulong at, Span<byte> b) { Request(); base.Read(at, b); }
        public override void Write(ulong at, ReadOnlySpan<byte> b) { Request(); base.Write(at, b); }
        public override void Flush() { Request(); base.Flush(); }
        public void Cooperate() { Yields++; SinceYield = 0; }
    }
    [Fact] public void CooperativeBudgetIncludesJournalAndCleanupIo()
    {
        var d = new BudgetDevice(Fresh().Bytes); var v = new PrototypeVolume<BudgetDevice>(d, Workspace()); d.Audit = true;
        v.Put("large"u8, Pattern(100000)); v.TryGetInfo("large"u8, out var info);
        v.Truncate(info.Id, 10); v.Truncate(info.Id, 90000); v.Delete("large"u8); v.Check();
        Assert.InRange(d.Peak, 1, 4); Assert.True(d.Yields > 50); new IndependentImage(d.Bytes);
    }
    [Fact] public void NameSearchResumesAcrossPagesAndInvalidatesAfterMutation()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        for (int i = 0; i < 80; i++) Assert.Equal(MutationResult.Success, v.Create(Encoding.ASCII.GetBytes($"file{i}"), out _));
        NameSearch search = default; v.StartNameSearch(1, "__uffffffffffffffff"u8, ref search); ScanResult result; int steps = 0, compared = 0;
        do
        {
            int before = d.Reads; result = v.FindNameStep(ref search, "__uffffffffffffffff"u8, out _);
            Assert.InRange(d.Reads - before, 0, 4); Assert.InRange(search.PagesRead, 0, 4); Assert.InRange(search.CandidatesCompared, 0, 64);
            compared += search.CandidatesCompared; Assert.True(++steps < 200);
        } while (result == ScanResult.Pending);
        Assert.Equal(ScanResult.End, result); Assert.Equal(80, compared); Assert.True(steps > 1);
        v.StartNameSearch(1, "file40"u8, ref search); v.Create("mutation"u8, out _);
        int reads = d.Reads; Assert.Equal(ScanResult.Invalidated, v.FindNameStep(ref search, "file40"u8, out _)); Assert.Equal(reads, d.Reads);
        v.StartNameSearch(1, "file40"u8, ref search); Assert.Equal(ScanResult.Invalidated, v.FindNameStep(ref search, "file41"u8, out _));
        do { result = v.FindNameStep(ref search, "file40"u8, out _); } while (result == ScanResult.Pending);
        Assert.Equal(ScanResult.Found, result); v.Check();
    }
    [Fact] public void FragmentedBitmapSearchIsFourPagesPerCallAndNeverReservesItsHint()
    {
        var d = Fresh(); var l = new Layout(512, 512, (ulong)d.Bytes.Length / 512, 12345);
        // Allocator-unit fixture: one free AU, too short for a metadata page.
        // This intentionally has no file-ownership oracle; it tests scan accounting.
        for (int i = 0; i < 16; i++)
        {
            Span<byte> page = d.Bytes.AsSpan((int)(l.Bitmap(0, i) * 512), 1024); page.Slice(64,512).Fill(255);
            if (i == 1) page[64 + 101 / 8] &= unchecked((byte)~(128 >> (101 % 8))); Seal(page);
        }
        Span<byte> group = d.Bytes.AsSpan((int)(l.GroupDescriptor(0) * 512),1024); Put32(group,68,1);
        for (int i=0;i<16;i++) Put32(group,80+i*4,i==1?1u:0); Seal(group);
        var v = new PrototypeVolume<MemoryDevice>(d, Workspace()); var search = v.StartFreeSearch(2,true); int writes=d.Writes, steps=0; ScanResult result;
        do { int before=d.Reads; result=v.FindFreeStep(ref search,out _,out _); Assert.InRange(d.Reads-before,0,4); Assert.True(++steps<10); } while(result==ScanResult.Pending);
        Assert.Equal(ScanResult.End,result); Assert.True(steps>=4); Assert.Equal(writes,d.Writes);
        search=v.StartFreeSearch(1); do { result=v.FindFreeStep(ref search,out _,out _); } while(result==ScanResult.Pending); Assert.Equal(ScanResult.Found,result);
    }
    [Fact] public void AncestryWalkUsesBoundedCallerState()
    {
        var d=Fresh(); var v=new PrototypeVolume<MemoryDevice>(d,Workspace()); ulong parent=1, ancestor=0;
        for(int i=0;i<30;i++) { v.Create(parent,"child"u8,true,out parent); if(i==10)ancestor=parent; }
        var search=v.StartAncestrySearch(parent,ancestor); ScanResult result; int calls=0;
        do { int reads=d.Reads; result=v.CheckAncestryStep(ref search); Assert.InRange(d.Reads-reads,0,4); Assert.True(++calls<100); } while(result==ScanResult.Pending);
        Assert.Equal(ScanResult.Found,result); Assert.True(calls>1);
        search=v.StartAncestrySearch(parent,ulong.MaxValue); do { result=v.CheckAncestryStep(ref search); } while(result==ScanResult.Pending); Assert.Equal(ScanResult.End,result);
    }
    [Fact] public void DeferredReclamationRetiresAtMostOneTransactionPerStep()
    {
        var d=Fresh(); var v=new PrototypeVolume<MemoryDevice>(d,Workspace(),deferCleanup:true);
        v.Put("file"u8,Pattern(900000)); Assert.Equal(MutationResult.Success,v.Delete("file"u8)); Assert.True(v.HasPendingCleanup);
        try { v.Create("blocked"u8,out _); Assert.Fail("Mutation overtook cleanup."); } catch(InvalidOperationException) { }
        int steps=0;
        while(true) { ulong before=v.Generation; bool progress=v.ReclaimStep(); Assert.InRange(v.Generation-before,0UL,1UL); Assert.True(++steps<100); if(!progress)break; }
        Assert.False(v.HasPendingCleanup); while(v.PruneDirectoryStep(1)) { } v.Check(); new IndependentImage(d.Bytes); Assert.True(steps>2);
        Assert.Equal(MutationResult.Success,v.Create("after"u8,out _));
    }
    [Fact] public void IndexedExtentLookupDoesNotScanTheWholeFile()
    {
        var d=Fresh(); var v=new PrototypeVolume<MemoryDevice>(d,Workspace()); v.Create("many"u8,out ulong id);
        for(uint i=0;i<180;i++) v.Preallocate(id,i*1024,1,out _);
        byte[] output=new byte[512]; int before=d.Reads; Assert.Equal(512,v.Read(id,179*1024,output));
        Assert.InRange(d.Reads-before,1,16); Assert.All(output,b=>Assert.Equal(0,b));
        before=d.Reads; Assert.Equal(512,v.Read(id,178*1024+512,output)); Assert.InRange(d.Reads-before,1,16);
    }
    private static void ReclaimOnce(CrashDevice device)
    {
        var v=new PrototypeVolume<CrashDevice>(device,Workspace(),deferCleanup:true);
        Assert.True(v.HasPendingCleanup); Assert.True(v.ReclaimStep());
    }
    [Fact] public void DeferredCleanupStepRecoversAtEverySectorAndBarrier()
    {
        var d=Fresh(); var v=new PrototypeVolume<MemoryDevice>(d,Workspace(),deferCleanup:true);
        v.Put("orphan"u8,Pattern(10000)); v.Put("keep"u8,"intact"u8); v.Delete("orphan"u8);
        byte[] baseline=(byte[])d.Bytes.Clone(); var complete=new CrashDevice(baseline); ReclaimOnce(complete);
        for(int cut=1;cut<=complete.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(baseline) { Cut=cut, Persistence=mode };
            Assert.Throws<PowerLoss>(()=>ReclaimOnce(fault));
            var recoveredDevice=new MemoryDevice(fault.Durable); var recovered=new PrototypeVolume<MemoryDevice>(recoveredDevice,Workspace());
            Assert.False(recovered.HasPendingCleanup); recovered.Check(); var oracle=new IndependentImage(recoveredDevice.Bytes);
            Assert.False(oracle.Names.ContainsKey((1,"orphan")));
            Assert.Equal("intact"u8.ToArray(),oracle.Read(oracle.Names[(1,"keep")]));
        }
        output.WriteLine($"{complete.Events.Count} deferred-cleanup cuts x 5 persistence schedules = {complete.Events.Count*5} crash recoveries.");
    }
}
