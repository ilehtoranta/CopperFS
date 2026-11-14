using Amiga;
namespace CopperCPX.InstructionTests;

public static partial class CpxBootProbe
{
    private static uint MediaLifecycle(APTR control,APTR port,APTR data)
    {
        APTR.WriteUInt32(control,28,400);
        var file=DOS.Open("CPX0:Payload",DOS.FileMode.OldFile); if(!file.HasValue) return 400;
        if(DOS.DoPkt(port,31,-1,0,0,0,0)!=0||(uint)DOS.IoErr()!=202) return 401;
        if(DOS.Read(file.Value,data,1)!=1||APTR.ReadUInt8(data,0)!=11||DOS.Close(file.Value)==0) return 402;
        APTR.WriteUInt32(control,28,403);
        if(DOS.DoPkt(port,31,-1,0,0,0,0)==0||DOS.DoPkt(port,31,-1,0,0,0,0)==0) return 403;
        if(!CheckUnsupportedPackets(control,port,0,226)) return 963;
        if(DOS.DoPkt(port,25,(int)(data.Raw>>2),0,0,0,0)==0||APTR.ReadUInt32(data,24)!=uint.MaxValue) return 404;
        if(DOS.Open("CPX0:Payload",DOS.FileMode.OldFile).HasValue||(uint)DOS.IoErr()!=226) return 405;
        if(DOS.DoPkt(port,1027,0,0,0,0,0)==0) return 406;
        APTR.WriteUInt32(control,28,407);
        if(DOS.DoPkt(port,31,0,0,0,0,0)==0||DOS.DoPkt(port,31,0,0,0,0,0)==0) return 407;
        file=DOS.Open("CPX0:Payload",DOS.FileMode.OldFile); if(!file.HasValue) return 408;
        if(DOS.Read(file.Value,data,1)!=1||APTR.ReadUInt8(data,0)!=11) return 409;

        // The emulated hardfile refuses TD_ADDCHANGEINT. Inject precisely the
        // callback's latch+Signal output to exercise the actual HUNK's event
        // consumer under the ROM. The emitted interrupt itself and delayed
        // AbortIO/WaitIO ownership are executed by separate native tests.
        APTR.WriteUInt32(control,36,600); APTR.WriteUInt32(control,28,410);
        uint arena=APTR.ReadUInt32(control,0);
        APTR.WriteUInt32(APTR.FromPointer(arena+3808),0,1);
        Exec.Signal(APTR.FromPointer(APTR.ReadUInt32(port,16)),APTR.ReadUInt32(APTR.FromPointer(arena+3824),4));
        if(DOS.DoPkt(port,25,(int)(data.Raw>>2),0,0,0,0)==0||APTR.ReadUInt32(data,24)!=uint.MaxValue) return 410;
        if(DOS.Read(file.Value,data,1)!=-1||(uint)DOS.IoErr()!=226) return 411;
        if(DOS.DoPkt(port,31,0,0,0,0,0)!=0||(uint)DOS.IoErr()!=202) return 412;
        if(DOS.Close(file.Value)==0) return 413;
        APTR.WriteUInt32(control,28,414);
        if(DOS.DoPkt(port,31,0,0,0,0,0)==0) return 414;
        file=DOS.Open("CPX0:Payload",DOS.FileMode.OldFile); if(!file.HasValue) return 415;
        if(DOS.Read(file.Value,data,1)!=1||APTR.ReadUInt8(data,0)!=11||DOS.Close(file.Value)==0) return 416;
        return 0;
    }
}
