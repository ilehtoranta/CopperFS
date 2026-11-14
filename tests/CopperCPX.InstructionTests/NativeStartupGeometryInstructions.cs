using Amiga;
using Copper68k;
using CopperCPX.Amiga;
using CopperFS.Cpx4.Reference;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;

namespace CopperCPX.InstructionTests;

public static class StartupGeometryProbe
{
    public static uint Entry()
    {
        const uint arena=0x40000;
        if(!NativeCpxHandler.Geometry(APTR.FromPointer(0x8400),out var start,out var sectors,out uint sector,out uint maximum,out uint mask))
            return 115;
        NativeCpxTransport.Initialize(arena,APTR.FromPointer(0x8200),start,sectors,maximum,mask,true,sector);
        NativeCpxLifecycle.BeginStartup(arena,APTR.FromPointer(0x8300),APTR.FromPointer(0x8320));
        var dispatcher=new CpxDosDispatcher<NativeCpxMemory>(new NativeCpxMemory(),arena);
        return NativeCpxHandler.Run(arena,APTR.FromPointer(0x8000),APTR.FromPointer(0x8040),ref dispatcher);
    }
}

public class NativeStartupGeometryInstructions
{
    private sealed class Partition(int sector,int tailBytes=0) : IBlockDevice
    {
        internal readonly byte[] Bytes=new byte[4*1024*1024+tailBytes];
        public int SectorSize=>sector;
        public ulong SectorCount=>(ulong)Bytes.Length/(uint)sector;
        public void Read(ulong at,Span<byte> data)=>Bytes.AsSpan(checked((int)at*sector),data.Length).CopyTo(data);
        public void Write(ulong at,ReadOnlySpan<byte> data)=>data.CopyTo(Bytes.AsSpan(checked((int)at*sector)));
        public void Flush() { }
    }
    private static readonly Lazy<M68kCompilationResult> Compiled=new(()=>AmigaM68kCompiler.Compile(new()
    {
        AssemblyPath=typeof(StartupGeometryProbe).Assembly.Location,EntryPoint=typeof(StartupGeometryProbe).FullName+"::Entry",
        ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],
        IncludedExportNames=[],Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,
        RuntimeProfile=M68kRuntimeProfile.Freestanding,MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo
    }));

    [Theory]
    [InlineData("damaged primary",512,4096)]
    [InlineData("unreadable primary",1024,2048)]
    [InlineData("damaged backup",2048,2048)]
    [InlineData("unaligned partition end",512,512)]
    [InlineData("split large sectors",1024,4096)]
    [InlineData("TD64 only above 4 GiB",4096,4096)]
    [InlineData("narrow ends at 4 GiB",512,1024)]
    [InlineData("narrow crosses 4 GiB",512,1024)]
    [InlineData("narrow above 4 GiB",4096,4096)]
    [InlineData("late unsupported",512,1024)]
    [InlineData("split unsupported",512,4096)]
    [InlineData("short read",512,512)]
    [InlineData("media changed",512,512)]
    [InlineData("unreadable blank",512,512)]
    [InlineData("conflicting identity",512,1024)]
    [InlineData("conflicting geometry",512,1024)]
    public void DiscoveryAuthenticatesCopiesAndOnlyDowngradesAnEntireAddressablePartition(string scenario,int sector,int unit)
    {
        bool tail=scenario=="unaligned partition end";
        var disk=new Partition(sector,tail?sector:0);
        int bytes=disk.Bytes.Length,page=Math.Max(1024,unit);
        Cpx4Volume<Partition>.Format(disk,unit,new byte[36864],0x100000001);
        switch(scenario)
        {
            case "damaged primary": case "unaligned partition end": disk.Bytes[65536+82]^=0x12; break;
            case "damaged backup": disk.Bytes[bytes-page+8]^=1; break;
            case "unreadable blank": Array.Clear(disk.Bytes); break;
            case "conflicting identity": case "conflicting geometry":
                var other=new Partition(sector);
                int otherUnit=scenario=="conflicting identity"?unit:4096;
                Cpx4Volume<Partition>.Format(other,otherUnit,new byte[36864],0x200000001);
                other.Bytes.AsSpan(bytes-otherUnit).CopyTo(disk.Bytes.AsSpan(bytes-otherUnit)); break;
        }
        ulong start=scenario switch
        {
            "narrow ends at 4 GiB" => (1UL<<32)-(uint)bytes,
            "narrow crosses 4 GiB" => (1UL<<32)-(uint)bytes+(uint)sector,
            "narrow above 4 GiB" or "TD64 only above 4 GiB" => (1UL<<32)+65536,
            _ => 65536
        };
        int maximum=scenario=="split unsupported"||scenario=="split large sectors"?sector:4096;
        var compiled=Compiled.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes,0x10000);
        foreach(var reloc in compiled.Relocations)
        { uint p=0x10000+(uint)reloc.Offset; bus.Put(p,bus.Get(p)+0x10000); }
        bus.Put(4,0x9000); bus.Put(0xf0000,0xf1000);
        bus.Bytes[0x800f]=7; bus.Put(0x8014,0x8018); bus.Bytes[0x804f]=8; bus.Put(0x8054,0x8058);
        bus.Put(0x830a,0x8320); bus.Put(0x8324,0x8100);
        uint cylinder=(uint)(start/(uint)sector);
        bus.Put(0x8400,16); bus.Put(0x8404,(uint)sector/4); bus.Put(0x840c,1); bus.Put(0x8410,sector==512&&!tail?2u:1u); bus.Put(0x8414,1);
        bus.Put(0x8424,cylinder); bus.Put(0x8428,cylinder+(uint)disk.SectorCount-1); bus.Put(0x8434,(uint)maximum); bus.Put(0x8438,0xfffffffe);

        // Expected command trace up to the first recovery read or a refused startup.
        var expected=new List<(int Command,int Offset,int Length,int Error)>();
        int command=24,expectedDosError=0;
        for(int size=Math.Max(1024,sector);size<=4096&&expectedDosError==0;size*=2)
        {
            int backup=(bytes/size-1)*size;
            foreach(int location in new[]{65536,backup})
            {
                for(int done=0;done<size;done+=maximum)
                {
                    int length=Math.Min(maximum,size-done),number=expected.Count;
                    int error=scenario switch
                    {
                        "narrow ends at 4 GiB" or "narrow crosses 4 GiB" or "narrow above 4 GiB" when number==0 => 253,
                        "split unsupported" or "late unsupported" when number==1 => 253,
                        "short read" when number==0 => -1,
                        "media changed" when number==0 => 29,
                        "unreadable primary" or "unreadable blank" when location==65536 => 25,
                        _ => 0
                    };
                    expected.Add((command,location+done,length,error));
                    if(error==253&&scenario=="narrow ends at 4 GiB")
                    { command=2; expected.Add((command,location+done,length,0)); }
                    else if(error!=0&&error!=25) { expectedDosError=error==29?226:213; break; }
                    if(error==25) break;
                }
                if(location==backup&&((scenario=="conflicting identity"&&size==page)||
                    (size==4096&&scenario is "conflicting geometry" or "unreadable blank"))) expectedDosError=213;
                if(expectedDosError!=0) break;
            }
        }
        if(expectedDosError==0) expected.Add((command,65536+page,Math.Min(page,maximum),0));

        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus);
        cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        bool active=false,ready=false,finished=false; int sent=0,waits=0;
        for(int n=0;n<20_000_000&&!finished;n++)
        {
            int lvo=unchecked((int)(0x9000-cpu.State.ProgramCounter));
            switch(lvo)
            {
                case 306: cpu.State.D[0]=0; break;
                case 462:
                    Assert.False(active); Assert.True(sent<expected.Count);
                    var request=expected[sent++]; Assert.Equal(request.Command,bus.Bytes[0x821d]);
                    Assert.Equal(start+(uint)request.Offset,((ulong)bus.Get(0x8220)<<32)|bus.Get(0x822c));
                    Assert.Equal((uint)request.Length,bus.Get(0x8224)); Assert.Equal(0,bus.Bytes[0x821f]);
                    active=true; ready=false; waits=0; finished=sent==expected.Count&&expectedDosError==0; break;
                case 468: Assert.True(active); cpu.State.D[0]=ready?0x8200u:0; break;
                case 318: Assert.True(active); ready=++waits>=2; cpu.State.D[0]=ready?256u:128u; break;
                case 474:
                    Assert.True(active&&ready); var completed=expected[sent-1]; active=false;
                    if(completed.Error==0) disk.Bytes.AsSpan(completed.Offset,completed.Length).CopyTo(bus.Bytes.AsSpan((int)bus.Get(0x8228)));
                    bus.Bytes[0x821f]=(byte)Math.Max(completed.Error,0);
                    bus.Put(0x8220,completed.Error==-1?(uint)completed.Length-1:(uint)completed.Length); cpu.State.D[0]=0; break;
                case 366:
                    Assert.False(active); Assert.NotEqual(0,expectedDosError); Assert.Equal(0u,bus.Get(0x832c));
                    Assert.Equal((uint)expectedDosError,bus.Get(0x8330)); finished=true; break;
                default: cpu.ExecuteInstruction(); continue;
            }
            cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
        }
        Assert.True(finished); Assert.Equal(expected.Count,sent);
    }
}
