using System.Reflection;

namespace CopperCPX.Tests;

public class MetadataScanTests
{
    [Fact]
    public void StridedScansMatchFirstFullKeyAndNeverMutateCallerMemory()
    {
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, 512, 32768, 1, metadataPageBytes:512));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)));
        uint table=ram.Read32(32+4040), objects=ram.Read32(32+4088);
        var staged=typeof(CpxEngine<Ram>).GetMethod("FindStaged", BindingFlags.Instance|BindingFlags.NonPublic)!;
        var cached=typeof(CpxEngine<Ram>).GetMethod("CachedObject", BindingFlags.Instance|BindingFlags.NonPublic)!;
        var random=new Random(58133);
        for(int turn=0; turn<1000; turn++)
        {
            ulong[] keys=new ulong[42]; bool[] valid=new bool[8];
            for(uint n=0; n<42; n++)
            {
                // Deliberately collide each half and include duplicate full keys.
                keys[n]=((ulong)(uint)random.Next(4)<<32)|(uint)random.Next(9);
                ram.Write32(table+n*32,(uint)(keys[n]>>32)); ram.Write32(table+n*32+4,(uint)keys[n]);
                if(n<8)
                {
                    valid[n]=random.Next(2)!=0;
                    ram.Write32(objects+n*272+12,(uint)(keys[n]>>32)); ram.Write32(objects+n*272+16,(uint)keys[n]);
                    ram.Write32(objects+n*272+268,valid[n]?1u:0u);
                }
            }
            int count=random.Next(43); bool active=random.Next(4)!=0;
            ram.Write32(32+176,active?1u:0u); ram.Write32(32+180,(uint)count);
            byte[] before=(byte[])ram.Bytes.Clone();
            foreach(ulong key in new[]{keys[0],keys[7],keys[41],ulong.MaxValue,0UL})
            {
                int expected=active?Array.FindIndex(keys,0,count,k=>k==key):-1;
                Assert.Equal(expected,(int)staged.Invoke(engine,[key])!);
                int objectIndex=Enumerable.Range(0,8).FirstOrDefault(n=>valid[n]&&keys[n]==key,-1);
                Assert.Equal(objectIndex<0?0u:objects+(uint)objectIndex*272+12,(uint)cached.Invoke(engine,[key])!);
            }
            Assert.Equal(before,ram.Bytes);
        }
    }
}
