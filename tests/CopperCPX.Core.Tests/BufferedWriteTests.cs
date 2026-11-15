using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class BufferedWriteTests
{
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void CompactStoragePreservesBufferedDurabilityAndBoundsAcrossRemount(int unit)
    {
        var disk=EngineTests.Fresh(unit); var ram=new Ram(new byte[1000000]);
        var engine=FileMutationTests.Mount(disk,ram,unit);
        uint page=(uint)Math.Max(1024,unit),bytes=CpxEngine<Ram>.CompactBufferedStorageBytes(page);
        Assert.Equal(CpxEngine<Ram>.BufferedStorageBytes(page)-4*(page+16),bytes);
        ram.Bytes.AsSpan((int)(300000+bytes),32).Fill(0xa5);
        Assert.Equal(CpxError.None,engine.EnableBufferedWrites(300000,bytes));
        var trace=new CrashDevice(disk.Bytes); var generation=engine.Transaction;
        "compact"u8.CopyTo(ram.Bytes.AsSpan(100000));
        engine.BeginCreate(Wide.From(1),100000,7); EngineTests.Drive(ref engine,ram,trace);
        Wide id=engine.MutatedObject;
        byte[] data=Enumerable.Range(0,unit*4).Select(n=>(byte)(n*17+3)).ToArray();
        data.CopyTo(ram.Bytes,100000);
        engine.BeginMappedWrite(id,default,100000,(uint)data.Length); EngineTests.Drive(ref engine,ram,trace);
        Assert.True(engine.HasBufferedChanges); Assert.True(Wide.Equal(generation,engine.Transaction));
        Assert.DoesNotContain(trace.Events,e=>e.Contains("flush"));
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine,ram,trace);
        var durable=new MemoryDevice(trace.Durable);
        engine.BeginRemount(); EngineTests.Drive(ref engine,ram,durable); Assert.True(engine.BufferedWrites);
        engine.BeginFileRead(id,default,100000,(uint)data.Length); EngineTests.Drive(ref engine,ram,durable);
        Assert.Equal(data,ram.Bytes.AsSpan(100000,data.Length).ToArray());
        Assert.All(ram.Bytes.AsSpan((int)(300000+bytes),32).ToArray(),b=>Assert.Equal(0xa5,b));
        new Cpx4Volume<MemoryDevice>(durable,new byte[36864]).Check(); new IndependentImage(durable.Bytes);
    }
    [Fact]
    public void BufferedCreatePreservesExhaustedReserveRejection()
    {
        var disk=new MemoryDevice(ExhaustedVolume.Create()); var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram);
        "new"u8.CopyTo(ram.Bytes.AsSpan(100000)); var generation=engine.Transaction;
        engine.BeginCreate(Wide.From(1),100000,3,true);
        for(int n=0;n<20000;n++) {
            var step=engine.Step(); if(step==StepResult.Failed) { Assert.Equal(CpxError.ReserveExhausted,engine.Error); Assert.True(engine.IsMounted); Assert.True(Wide.Equal(generation,engine.Transaction)); return; }
            Assert.NotEqual(StepResult.Complete,step); if(step!=StepResult.NeedIo) continue; var io=engine.Request;
            if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
            engine.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Fail("Exhausted reserve did not reject creation.");
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void InitialWriteDoesNotNormalizeMalformedInlineRecords(bool extentRoot)
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("bad"u8,out ulong id);
        Span<byte> Page(ulong at)=>disk.Bytes.AsSpan((int)at*512,1024);
        ulong root=Layout.U64(Page(reference.Geometry.State),80);
        if(extentRoot) Layout.Put64(Page(root),64+256+24,root); else Layout.Put16(Page(root),64+256+88,1);
        Layout.Put32(Page(root),8,Layout.Crc(Page(root),true));
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram); "side"u8.CopyTo(ram.Bytes.AsSpan(100000));
        engine.BeginCreate(Wide.From(1),100000,4); EngineTests.Drive(ref engine,ram,disk);
        Assert.Equal(CpxError.None,engine.BeginMappedWrite(Wide.From((uint)id),default,100000,512));
        for(int n=0;n<2000;n++) {
            var step=engine.Step(); if(step==StepResult.Failed) { Assert.Equal(CpxError.Corrupt,engine.Error); return; }
            Assert.NotEqual(StepResult.Complete,step); if(step!=StepResult.NeedIo) continue; var io=engine.Request;
            Assert.Equal(IoKind.Read,io.Kind); disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); engine.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Fail("Malformed inline record was accepted.");
    }
    [Fact]
    public void FailedPreparedMountCannotReuseCachedPagesAfterRepair()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("old"u8,out _);
        Span<byte> Page(ulong at)=>disk.Bytes.AsSpan((int)at*512,1024);
        ulong root=Layout.U64(Page(reference.Geometry.State),80); byte[] saved=Page(root).ToArray();
        Layout.Put16(Page(root),48,8); Layout.Put32(Page(root),8,Layout.Crc(Page(root),true));
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32); engine.Initialize(512,512,Wide.From(8192),1);
        engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(1024));
        CpxError Drive()
        {
            for(int n=0;n<20000;n++) {
                var step=engine.Step(); if(step==StepResult.Complete||step==StepResult.Failed) return engine.Error;
                if(step!=StepResult.NeedIo) continue; var io=engine.Request;
                if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
                engine.CompleteIo(io.Token,io.Bytes,true);
            }
            Assert.Fail("Mount continuation did not terminate."); return CpxError.Busy;
        }
        engine.BeginOperation(CpxOperation.Mount); Assert.Equal(CpxError.Corrupt,Drive());
        saved.CopyTo(Page(root)); reference.Create("new"u8,out ulong id);
        Assert.Equal(CpxError.None,engine.BeginRemount()); Assert.Equal(CpxError.None,Drive()); Assert.True(engine.BufferedWrites);
        "new"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginLookup(Wide.From(1),100000,3); Assert.Equal(CpxError.None,Drive());
        Assert.True(engine.LookupFound); Assert.Equal((uint)id,engine.LookupId.Low); new IndependentImage(disk.Bytes);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void CachedNamespaceFastPathRejectsMalformedDirectoryShape(bool oversized)
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("old"u8,out _);
        Span<byte> Page(ulong at)=>disk.Bytes.AsSpan((int)at*512,1024);
        ulong objects=Layout.U64(Page(reference.Geometry.State),80),directory=Layout.U64(Page(objects),64+32);
        if(oversized) Layout.Put16(Page(directory),48,8); else Layout.Put64(Page(directory),24,999);
        Layout.Put32(Page(directory),8,Layout.Crc(Page(directory),true));
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram);
        engine.BeginProtection(Wide.From(1),0); EngineTests.Drive(ref engine,ram,disk); Assert.True(engine.HasBufferedChanges);
        engine.BeginOperation(CpxOperation.ReadMetadata,Wide.From((uint)directory)); EngineTests.Drive(ref engine,ram,disk);
        "old"u8.CopyTo(ram.Bytes.AsSpan(100000)); "new"u8.CopyTo(ram.Bytes.AsSpan(100100));
        Assert.Equal(CpxError.None,engine.BeginRename(Wide.From(1),100000,3,Wide.From(1),100100,3));
        for(int n=0;n<2000;n++) {
            var step=engine.Step(); if(step==StepResult.Failed) { Assert.Equal(CpxError.Corrupt,engine.Error); return; }
            Assert.NotEqual(StepResult.Complete,step); if(step!=StepResult.NeedIo) continue;
            var io=engine.Request; Assert.Equal(IoKind.Read,io.Kind); disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); engine.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Fail("Malformed directory did not fail within the continuation bound.");
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void BufferedPageChecksumsMatchIndependentOracleAcrossZeroRunBoundaries(int unit)
    {
        var disk=EngineTests.Fresh(unit); var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram,unit);
        var crc=typeof(CpxEngine<Ram>).GetMethod("Crc",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        int page=Math.Max(1024,unit);
        for(int pattern=0;pattern<32;pattern++)
        {
            var input=ram.Bytes.AsSpan(100000,page); input.Clear();
            for(int n=0;n<page;n++) if(pattern==31||n<64||n%64==pattern||n%64==63-pattern) input[n]=(byte)(n*17+pattern*23+1);
            Assert.Equal(Layout.Crc(input,true),(uint)crc.Invoke(engine,[100000u,(uint)page,true])!);
        }
    }
    [Fact]
    public void PreparedCacheSurvivesMountAndRemountRevalidatesExternalChanges()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("old"u8,out _);
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        Assert.Equal(CpxError.None,engine.Initialize(512,512,Wide.From((uint)disk.SectorCount),1));
        Assert.Equal(CpxError.None,engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(1024)));
        Assert.Equal(CpxError.None,engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine,ram,disk); Assert.True(engine.BufferedWrites);
        "new"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginLookup(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,disk); Assert.False(engine.LookupFound);
        reference.Create("new"u8,out ulong id);
        Assert.Equal(CpxError.None,engine.BeginRemount()); EngineTests.Drive(ref engine,ram,disk);
        engine.BeginLookup(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,disk); Assert.True(engine.LookupFound); Assert.Equal((uint)id,engine.LookupId.Low);
        new IndependentImage(disk.Bytes);
    }
    [Theory][InlineData(512)][InlineData(4096)]
    public void SequentialAppendsCrossBitmapWindowsAndPartialOverwritesSurviveRemount(int unit)
    {
        var disk=new Disk { Bytes=new byte[16*1024*1024] }; Cpx4Volume<Disk>.Format(disk,unit,new byte[36864],12345);
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram,unit);
        "stream"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None,engine.BeginCreate(Wide.From(1),100000,6)); EngineTests.Drive(ref engine,ram,disk);
        Wide id=engine.MutatedObject;
        // The 512-byte profile crosses a bitmap boundary in one buffered group.
        int bytes=unit==512?2*1024*1024:2*65536;
        var expected=new byte[bytes];
        for(int n=0;n<bytes;n++) expected[n]=(byte)(n*17+3);
        for(int at=0;at<bytes;at+=65536) {
            expected.AsSpan(at,65536).CopyTo(ram.Bytes.AsSpan(100000));
            Assert.Equal(CpxError.None,engine.BeginMappedWrite(id,Wide.From((uint)at),100000,65536)); EngineTests.Drive(ref engine,ram,disk);
        }
        byte[] patch=Enumerable.Range(0,unit*2+17).Select(n=>(byte)(n*23+7)).ToArray(); patch.CopyTo(ram.Bytes,100000); patch.CopyTo(expected,unit-1);
        Assert.Equal(CpxError.None,engine.BeginMappedWrite(id,Wide.From((uint)unit-1),100000,(uint)patch.Length)); EngineTests.Drive(ref engine,ram,disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine,ram,disk); engine.BeginRemount(); EngineTests.Drive(ref engine,ram,disk);
        for(int at=0;at<bytes;at+=65536) {
            Assert.Equal(CpxError.None,engine.BeginFileRead(id,Wide.From((uint)at),100000,65536)); EngineTests.Drive(ref engine,ram,disk);
            Assert.Equal(expected.AsSpan(at,65536).ToArray(),ram.Bytes.AsSpan(100000,65536).ToArray());
        }
        new Cpx4Volume<Disk>(disk,new byte[36864]).Check(); new IndependentImage(disk.Bytes);
    }
    private static CpxEngine<Ram> Mount(IBlockDevice disk,Ram ram,int unit=512)
    {
        var engine=FileMutationTests.Mount(disk,ram,unit);
        Assert.Equal(CpxError.None,engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes((uint)Math.Max(1024,unit))));
        return engine;
    }
    [Fact]
    public void CapacityRetriesKeepIoTokensMonotonicAcrossDistantNamespaceEdits()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]);
        reference.Create(1,"left"u8,true,out ulong left); reference.Create(1,"right"u8,true,out ulong right);
        for(int n=0;n<120;n++) reference.Create(left,System.Text.Encoding.ASCII.GetBytes("file"+n),false,out _);
        reference.Create(right,"stay"u8,false,out _);
        // The offline one-child wrapper follows CpxBootTests.MakeDeep. Deep
        // independent trees exceed the clean cache and force reads after admission.
        var layout=reference.Geometry; var decoded=new IndependentImage(disk.Bytes);
        Span<byte> Page(ulong address)=>disk.Bytes.AsSpan(checked((int)address*512),1024);
        int reserve=Layout.U16(Page(layout.Reserve),64); ulong generation=reference.Generation;
        ulong Wrap(ulong child,PageType type,ulong owner,int width)
        {
            for(int level=Layout.U16(Page(child),50)+1;level<=7;level++)
            {
                ulong next=Layout.U64(Page(layout.Reserve),72+(--reserve)*8); Layout.Put64(Page(layout.Reserve),72+reserve*8,0);
                Layout.Header(Page(next),type,next,owner,generation,1,level); Layout.Put64(Page(next),64+width-8,child); Layout.Seal(Page(next)); child=next;
            }
            return child;
        }
        foreach(ulong address in decoded.Metadata)
        {
            if(Layout.U16(Page(address),4)!=11) continue;
            for(int slot=0;slot<Layout.U16(Page(address),48);slot++)
            {
                int offset=64+slot*256; ulong id=Layout.U64(Page(address),offset);
                if(id==left||id==right) Layout.Put64(Page(address),offset+32,Wrap(Layout.U64(Page(address),offset+32),PageType.DirectoryBranch,id,24));
            }
            Layout.Seal(Page(address));
        }
        Layout.Put64(Page(layout.State),80,Wrap(Layout.U64(Page(layout.State),80),PageType.ObjectBranch,0,16)); Layout.Seal(Page(layout.State));
        Layout.Put16(Page(layout.Reserve),64,(ushort)reserve); Layout.Seal(Page(layout.Reserve));
        new Cpx4Volume<Disk>(disk,new byte[36864]).Check(); new IndependentImage(disk.Bytes);
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram); uint last=0;
        void Drive()
        {
            for(int n=0;n<20000;n++)
            {
                var step=engine.Step(); if(step==StepResult.Complete) return;
                Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
                var io=engine.Request; Assert.True(io.Token>last,$"I/O token {io.Token} reused after {last}."); last=io.Token;
                ulong sector=((ulong)io.Sector.High<<32)|io.Sector.Low;
                if(io.Kind==IoKind.Read) disk.Read(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                else if(io.Kind==IoKind.Write) disk.Write(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
                Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true));
            }
            Assert.Fail("Namespace continuation did not terminate.");
        }
        for(int n=0;n<120;n++)
        {
            var name=System.Text.Encoding.ASCII.GetBytes("file"+n); name.CopyTo(ram.Bytes,100000);
            Assert.Equal(CpxError.None,engine.BeginRename(Wide.From((uint)left),100000,(uint)name.Length,Wide.From((uint)right),100000,(uint)name.Length)); Drive();
        }
        engine.BeginOperation(CpxOperation.Flush); Drive();
        new Cpx4Volume<Disk>(disk,new byte[36864]).Check(); new IndependentImage(disk.Bytes);
    }
    [Fact]
    public void DosFormatDrainsBufferedChangesBeforeReinitializingTheVolume()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram);
        "old"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,disk);
        Assert.True(engine.HasBufferedChanges);
        var dispatcher=new CpxDosDispatcher<Ram>(ram,32); dispatcher.Initialize(0x1234,0x5678);
        ram.Write8(100000,3); "New"u8.CopyTo(ram.Bytes.AsSpan(100001));
        ram.Write32(120008,1020); ram.Write32(120020,100000>>2); ram.Write32(120024,CpxArena.Magic);
        Assert.Equal(CpxError.None,dispatcher.BeginPacket(120000));
        bool completed=false;
        for(int n=0;n<20000;n++)
        {
            var step=dispatcher.Step(); if(step==StepResult.Complete) { completed=true; break; }
            Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
            var io=dispatcher.Request; ulong sector=((ulong)io.Sector.High<<32)|io.Sector.Low;
            if(io.Kind==IoKind.Read) disk.Read(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Write) disk.Write(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else disk.Flush();
            Assert.Equal(CpxError.None,dispatcher.CompleteIo(io.Token,io.Bytes,true));
        }
        Assert.True(completed); Assert.Equal(0u,dispatcher.DosError); Assert.Equal(uint.MaxValue,dispatcher.Result);
        Assert.True(engine.BufferedWrites); Assert.False(engine.HasBufferedChanges);
        var decoded=new IndependentImage(disk.Bytes); Assert.Equal("New",decoded.Label); Assert.Empty(decoded.Names);
        new Cpx4Volume<Disk>(disk,new byte[36864]).Check();
    }
    [Fact]
    public void ValidatedMetadataIsReusedAndNamespaceChangesInvalidateOldImages()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("old"u8,out ulong old);
        var device=new MemoryDevice(disk.Bytes); var ram=new Ram(new byte[1000000]); var engine=Mount(device,ram);
        "old"u8.CopyTo(ram.Bytes.AsSpan(100000));
        engine.BeginLookup(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,device); Assert.Equal((uint)old,engine.LookupId.Low);
        int reads=device.Reads;
        engine.BeginLookup(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,device); Assert.Equal(reads,device.Reads);
        engine.BeginDelete(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,device);
        "new"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,device);
        engine.BeginOperation(CpxOperation.Flush); EngineTests.Drive(ref engine,ram,device);
        engine.BeginLookup(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,device); Assert.True(engine.LookupFound);
        Assert.Equal(CpxError.None,engine.BeginRemount()); EngineTests.Drive(ref engine,ram,device);
        engine.BeginLookup(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,device); Assert.True(engine.LookupFound);
        new Cpx4Volume<MemoryDevice>(device,new byte[36864]).Check(); new IndependentImage(device.Bytes);
    }
    [Theory][InlineData(512)][InlineData(4096)]
    public void WritesCoalesceUntilFlushAndCanBeReadBeforePublication(int unit)
    {
        var disk=EngineTests.Fresh(unit); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("file"u8,out ulong id);
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram,unit);
        Wide generation=engine.Transaction;
        byte[] payload=Enumerable.Range(0,unit*4).Select(n=>(byte)(n*13+1)).ToArray(); payload.CopyTo(ram.Bytes,100000);
        var trace=new CrashDevice(disk.Bytes);
        for(uint n=0;n<4;n++)
        {
            Assert.Equal(CpxError.None,engine.BeginMappedWrite(Wide.From((uint)id),Wide.From(n*(uint)unit),100000+n*(uint)unit,(uint)unit));
            EngineTests.Drive(ref engine,ram,trace);
        }
        Assert.DoesNotContain(trace.Events,s=>s.Contains("flush"));
        Assert.True(engine.HasBufferedChanges); Assert.Equal(generation.Low,engine.Transaction.Low);
        Assert.Equal(CpxError.Busy,engine.BeginRemount());
        engine.BeginFileRead(Wide.From((uint)id),default,150000,(uint)payload.Length); EngineTests.Drive(ref engine,ram,trace);
        Assert.Equal(payload,ram.Bytes.AsSpan(150000,payload.Length).ToArray());
        Assert.Equal(CpxError.None,engine.BeginOperation(CpxOperation.Flush)); EngineTests.Drive(ref engine,ram,trace);
        Assert.False(engine.HasBufferedChanges); Assert.Equal(generation.Low+1,engine.Transaction.Low);
        var durable=new MemoryDevice(trace.Durable); FileMutationTests.Mount(durable,new Ram(new byte[1000000]),unit);
        var check=new Cpx4Volume<MemoryDevice>(durable,new byte[36864]); byte[] actual=new byte[payload.Length];
        Assert.Equal(actual.Length,check.Read(id,0,actual)); Assert.Equal(payload,actual); check.Check(); new IndependentImage(durable.Bytes);
    }
    [Fact]
    public void CapacityCommitRetriesTheWholeNamespaceOperationAndPreservesRejectedRequests()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram);
        Wide generation=engine.Transaction;
        for(int n=0;n<100;n++)
        {
            byte[] name=System.Text.Encoding.ASCII.GetBytes("entry"+n); name.CopyTo(ram.Bytes,100000);
            Assert.Equal(CpxError.None,engine.BeginCreate(Wide.From(1),100000,(uint)name.Length)); EngineTests.Drive(ref engine,ram,disk);
        }
        Assert.True(engine.Transaction.Low>generation.Low);
        "entry99"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None,engine.BeginCreate(Wide.From(1),100000,7));
        for(int n=0;n<1000;n++)
        {
            var result=engine.Step(); if(result==StepResult.Failed) break;
            if(result==StepResult.NeedIo)
            {
                var io=engine.Request; Assert.Equal(IoKind.Read,io.Kind);
                disk.Read(((ulong)io.Sector.High<<32)|io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                engine.CompleteIo(io.Token,io.Bytes,true);
            }
        }
        Assert.Equal(CpxError.AlreadyExists,engine.Error); Assert.True(engine.HasBufferedChanges);
        engine.BeginOperation(CpxOperation.Flush); EngineTests.Drive(ref engine,ram,disk);
        var check=new Cpx4Volume<Disk>(disk,new byte[36864]); check.Check(); Assert.Equal(101,new IndependentImage(disk.Bytes).Objects.Count);
    }
    [Fact]
    public void DispatcherInformationBuffersCannotOverwriteBufferedMountControl()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram);
        ram.Bytes.AsSpan(3072,128+256+260).Fill(0xcc);
        Assert.True(engine.BufferedWrites); Assert.Equal(300000u,engine.BufferedStorageAddress);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1),100000,4); EngineTests.Drive(ref engine,ram,disk);
        engine.BeginOperation(CpxOperation.Flush); EngineTests.Drive(ref engine,ram,disk); new IndependentImage(disk.Bytes);
    }
    [Fact]
    public void SmallInlineWriteRefreshesTheObjectCacheAndPersistsAtFlush()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]);
        reference.Create("file"u8,out ulong id); reference.Write(id,0,"abc"u8,out _);
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram);
        engine.BeginOperation(CpxOperation.ReadObject,Wide.From((uint)id)); EngineTests.Drive(ref engine,ram,disk);
        "XY"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginMappedWrite(Wide.From((uint)id),Wide.From(1),100000,2); EngineTests.Drive(ref engine,ram,disk);
        engine.BeginFileRead(Wide.From((uint)id),default,100000,3); EngineTests.Drive(ref engine,ram,disk); Assert.Equal("aXY"u8.ToArray(),ram.Bytes[100000..100003]);
        engine.BeginOperation(CpxOperation.Flush); EngineTests.Drive(ref engine,ram,disk);
        var check=new Cpx4Volume<Disk>(disk,new byte[36864]); byte[] actual=new byte[3]; check.Read(id,0,actual); Assert.Equal("aXY"u8.ToArray(),actual); check.Check();
    }
    [Fact]
    public void PreviouslyPersistentBlocksAreNotReusedBeforeTheirFreeingGroupRetires()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]);
        reference.Create("old"u8,out ulong old); reference.Write(old,0,new byte[2048],out _);
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram); var trace=new CrashDevice(disk.Bytes);
        "old"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginDelete(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,trace);
        "new"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1),100000,3); EngineTests.Drive(ref engine,ram,trace);
        Wide id=engine.MutatedObject; int before=trace.Events.Count(s=>s=="after flush");
        ram.Bytes.AsSpan(100000,2048).Fill(0x7b); engine.BeginMappedWrite(id,default,100000,2048); EngineTests.Drive(ref engine,ram,trace);
        Assert.True(trace.Events.Count(s=>s=="after flush")>before);
        var durable=new MemoryDevice(trace.Durable); FileMutationTests.Mount(durable,new Ram(new byte[1000000]));
        var decoded=new IndependentImage(durable.Bytes); Assert.DoesNotContain((1UL,"old"),decoded.Names.Keys);
        new Cpx4Volume<MemoryDevice>(durable,new byte[36864]).Check();
    }
    [Fact]
    public void ReusingTheBufferedArenaReplaysACommittedGroupWithRealChecksums()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("file"u8,out ulong id);
        byte[] payload=Enumerable.Repeat((byte)0x79,1536).ToArray();
        var ram=new Ram(new byte[1000000]); var engine=Mount(disk,ram); payload.CopyTo(ram.Bytes,100000);
        var trace=new CrashDevice(disk.Bytes);
        engine.BeginMappedWrite(Wide.From((uint)id),default,100000,(uint)payload.Length); EngineTests.Drive(ref engine,ram,trace);
        engine.BeginOperation(CpxOperation.Flush); EngineTests.Drive(ref engine,ram,trace);
        int commitCut=trace.Events.Select((label,index)=>(label,index)).Where(e=>e.label=="after flush").Skip(2).First().index+1;
        ram=new(new byte[1000000]); engine=Mount(disk,ram); payload.CopyTo(ram.Bytes,100000);
        var fault=new CrashDevice(disk.Bytes){Cut=commitCut,Persistence=0};
        engine.BeginMappedWrite(Wide.From((uint)id),default,100000,(uint)payload.Length); EngineTests.Drive(ref engine,ram,fault);
        engine.BeginOperation(CpxOperation.Flush);
        Assert.Throws<PowerLoss>(()=>EngineTests.Drive(ref engine,ram,fault));
        var pending=engine.Request; Assert.Equal(CpxError.Io,engine.CompleteIo(pending.Token,0,false));
        Assert.Equal(CpxError.None,engine.BeginRemount()); var recovered=new MemoryDevice(fault.Durable); EngineTests.Drive(ref engine,ram,recovered);
        Assert.True(engine.BufferedWrites); Assert.False(engine.HasBufferedChanges);
        var check=new Cpx4Volume<MemoryDevice>(recovered,new byte[36864]); check.Check(); new IndependentImage(recovered.Bytes);
        byte[] actual=new byte[payload.Length]; Assert.Equal(actual.Length,check.Read(id,0,actual)); Assert.Equal(payload,actual);
    }
    [Fact]
    public void GroupFlushRecoversAtEverySectorAndBarrierAndOnlyAcknowledgesDurableData()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("file"u8,out ulong id);
        byte[] payload=Enumerable.Range(0,1536).Select(n=>(byte)(n*19+7)).ToArray();
        void Write(IBlockDevice device)
        {
            var ram=new Ram(new byte[1000000]); var engine=Mount(device,ram); payload.CopyTo(ram.Bytes,100000);
            engine.BeginMappedWrite(Wide.From((uint)id),default,100000,1024); EngineTests.Drive(ref engine,ram,device);
            engine.BeginMappedWrite(Wide.From((uint)id),Wide.From(1024),101024,512); EngineTests.Drive(ref engine,ram,device);
            engine.BeginOperation(CpxOperation.Flush); EngineTests.Drive(ref engine,ram,device);
        }
        var trace=new CrashDevice(disk.Bytes); Write(trace);
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int persistence=0;persistence<5;persistence++)
        {
            var fault=new CrashDevice(disk.Bytes){Cut=cut,Persistence=persistence}; Assert.Throws<PowerLoss>(()=>Write(fault));
            var recovered=new MemoryDevice(fault.Durable); FileMutationTests.Mount(recovered,new Ram(new byte[1000000]));
            var check=new Cpx4Volume<MemoryDevice>(recovered,new byte[36864]); check.Check(); new IndependentImage(recovered.Bytes);
            byte[] actual=new byte[payload.Length]; int length=check.Read(id,0,actual);
            Assert.True(length==0||length==payload.Length); if(length!=0) Assert.Equal(payload,actual);
        }
    }
}
