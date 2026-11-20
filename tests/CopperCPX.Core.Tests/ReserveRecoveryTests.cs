using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class ReserveRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MountChecksEveryReserveEntryEvenWhenAddressesCollide(bool prepared, bool duplicate)
    {
        var disk=CollidingReserve();
        if(duplicate)
        {
            var reserve=disk.Bytes.AsSpan((128+16)*512,1024);
            Layout.Put64(reserve,72+63*8,Layout.U64(reserve,72)); Layout.Seal(reserve);
        }
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,512,Wide.From((uint)disk.SectorCount),1);
        if(prepared) engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(1024));
        engine.BeginOperation(CpxOperation.Mount);
        if(duplicate)
        {
            for(int n=0;n<20000;n++)
            {
                var step=engine.Step();
                if(step==StepResult.Failed) { Assert.Equal(CpxError.Corrupt,engine.Error); Assert.False(engine.IsMounted); return; }
                Assert.NotEqual(StepResult.Complete,step);
                if(step!=StepResult.NeedIo) continue;
                var io=engine.Request; Assert.Equal(IoKind.Read,io.Kind);
                disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); engine.CompleteIo(io.Token,io.Bytes,true);
            }
            Assert.Fail("Duplicate reserve was not rejected.");
        }
        else
        {
            EngineTests.Drive(ref engine,ram,disk);
            Assert.True(engine.IsMounted);
            "after"u8.CopyTo(ram.Bytes.AsSpan(100000));
            engine.BeginCreate(Wide.From(1),100000,5); EngineTests.Drive(ref engine,ram,disk);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine,ram,disk);
            engine.BeginRemount(); EngineTests.Drive(ref engine,ram,disk);
            new Cpx4Volume<Disk>(disk,new byte[36864]).Check(); new IndependentImage(disk.Bytes);
        }
    }

    private static Disk CollidingReserve()
    {
        var disk=new Disk { Bytes=new byte[16*1024*1024] };
        Cpx4Volume<Disk>.Format(disk,512,new byte[36864],12345);
        var geometry=new Cpx4Volume<Disk>(disk,new byte[36864]).Geometry;
        Span<byte> Page(ulong at)=>disk.Bytes.AsSpan((int)(at*512),1024);
        void Used(ulong at,bool used)
        {
            var map=Page(geometry.Bitmap(0,(int)(at>>12)));
            int bit=(int)(at&4095),slot=64+(bit>>3),mask=0x80>>(bit&7);
            map[slot]=used?(byte)(map[slot]|mask):(byte)(map[slot]&~mask);
        }
        int count=Layout.U16(Page(geometry.Reserve),64);
        for(int n=0;n<count;n++)
        {
            ulong address=Layout.U64(Page(geometry.Reserve),72+n*8);
            Used(address,false); Used(address+1,false);
        }
        // Sixty-four distinct pages share the same low address hash and span
        // five bitmap windows. Moving the last entry to the first adds a
        // duplicate after the bitmap window has changed several times.
        for(int n=0;n<64;n++)
        {
            ulong address=4352+(ulong)n*256;
            Layout.Put64(Page(geometry.Reserve),72+n*8,address);
            Used(address,true); Used(address+1,true);
        }
        Layout.Put16(Page(geometry.Reserve),64,64); Layout.Seal(Page(geometry.Reserve));
        uint total=0;
        for(int window=0;window<16;window++)
        {
            var map=Page(geometry.Bitmap(0,window)); uint free=0;
            foreach(byte value in map.Slice(64,512)) free+=8u-(uint)System.Numerics.BitOperations.PopCount(value);
            Layout.Seal(map); total+=free; Layout.Put32(Page(geometry.GroupDescriptor(0)),80+window*4,free);
        }
        Layout.Put32(Page(geometry.GroupDescriptor(0)),68,total); Layout.Seal(Page(geometry.GroupDescriptor(0)));
        Layout.Put64(Page(geometry.State),96,total); Layout.Seal(Page(geometry.State));
        new Cpx4Volume<Disk>(disk,new byte[36864]).Check(); new IndependentImage(disk.Bytes);
        return disk;
    }
}
