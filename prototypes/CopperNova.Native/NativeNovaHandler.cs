using Amiga;
using CopperSharp.Compiler;
using CopperSharp.Sdk.Amiga;
using CopperCPX;
using CopperCPX.Amiga;
using CopperNova.Prototype;

namespace CopperNova.Amiga;

public static class NativeNovaHandler
{
    public static uint Entry()
    {
        APTR dos=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),36);
        if(dos.IsNull) return 20;
        DOS.DOSLibraryBase=dos;
        APTR task=Exec.FindTask(CString.FromPointer(0)),port=APTR.FromPointer(task.Raw+92);
        Exec.WaitPort(port); APTR message=APTR.FromPointer(Exec.GetMsg(port));
        APTR packet=message.IsNull?APTR.Null:APTR.FromPointer(APTR.ReadUInt32(message,10));
        if(packet.IsNull) { Exec.CloseLibrary(dos); return 20; }
        uint arena=Exec.AllocMem(NovaEngine<NativeCpxMemory>.ArenaBytes,Exec.MemoryFlags.Public|Exec.MemoryFlags.Fast|Exec.MemoryFlags.Clear);
        if(arena==0) arena=Exec.AllocMem(NovaEngine<NativeCpxMemory>.ArenaBytes,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear);
        APTR ioPort=arena==0?APTR.Null:Exec.CreateMsgPort(),request=ioPort.IsNull?APTR.Null:Exec.CreateIORequest(ioPort,56);
        bool opened=false; APTR volume=APTR.Null,node=APTR.Null; uint failure=103;
        if(request.IsNotNull)
        {
            APTR startup=APTR.FromPointer(APTR.ReadUInt32(packet,24)<<2);
            APTR env=startup.IsNull?APTR.Null:APTR.FromPointer(APTR.ReadUInt32(startup,8)<<2);
            if(Geometry(env,out Wide start,out uint blocks,out uint maximum,out uint mask))
            {
                APTR bstr=APTR.FromPointer(APTR.ReadUInt32(startup,4)<<2),name=APTR.FromPointer(arena+1536);
                uint bytes=APTR.ReadUInt8(bstr,0);
                for(uint n=0;n<bytes;n++) APTR.WriteUInt8(name,(int)n,APTR.ReadUInt8(bstr,(int)n+1));
                APTR.WriteUInt8(name,(int)bytes,0);
                NativeCpxTransport.Initialize(arena,request,start,Wide.From(blocks),maximum,mask,true,512);
                if(NativeCpxTransport.ReserveDma(arena,APTR.ReadUInt32(env,48)))
                    opened=Exec.OpenDevice(CString.FromPointer(name.Raw),APTR.ReadUInt32(startup,0),request,APTR.ReadUInt32(startup,12))==0;
                if(opened)
                {
                    NativeCpxTransport.EnableQuickIo(arena);
                    BindDirect(arena,request,APTR.ReadUInt32(env,48),start,blocks,maximum,mask);
                    var engine=new NovaEngine<NativeCpxMemory>(new NativeCpxMemory(),arena);
                    engine.Bind(blocks,port.Raw,0);
                    if(engine.BeginMount()==CpxError.None&&Drive(arena,ioPort,task,ref engine)&&engine.IsMounted)
                    {
                        volume=DOS.MakeDosEntry(CString.FromLiteral("CopperNovaPrototype"),(int)DosListType.Volume);
                        if(volume.IsNotNull)
                        {
                            APTR.WriteUInt32(volume,8,port.Raw); APTR.WriteUInt32(volume,32,engine.VolumeMagic);
                            node=APTR.FromPointer(APTR.ReadUInt32(packet,28)<<2);
                            if(node.IsNotNull) APTR.WriteUInt32(node,8,port.Raw);
                            engine.Bind(blocks,port.Raw,volume.Raw);
                            Reply(message,packet,port,uint.MaxValue,0);
                            bool registered=DOS.AddDosEntry(volume)!=0;
                            Run(arena,port,ioPort,task,node,ref engine);
                            if(registered) DOS.RemDosEntry(volume);
                            failure=0;
                        }
                    }
                    else failure=213;
                }
                else failure=218;
            }
            else failure=115;
        }
        if(failure!=0) Reply(message,packet,port,0,failure);
        if(volume.IsNotNull) DOS.FreeDosEntry(volume);
        if(opened) Exec.CloseDevice(request);
        if(arena!=0) NativeCpxTransport.ReleaseDma(arena);
        if(request.IsNotNull) Exec.DeleteIORequest(request);
        if(ioPort.IsNotNull) Exec.DeleteMsgPort(ioPort);
        if(arena!=0) Exec.FreeMem(arena,NovaEngine<NativeCpxMemory>.ArenaBytes);
        Exec.CloseLibrary(dos); return failure==0?0u:20u;
    }
    private static bool Geometry(APTR env,out Wide start,out uint blocks,out uint maximum,out uint mask)
    {
        start=default; blocks=maximum=mask=0;
        if(env.IsNull||APTR.ReadUInt32(env,0)<16||APTR.ReadUInt32(env,4)!=128||APTR.ReadUInt32(env,16)!=1) return false;
        uint low=APTR.ReadUInt32(env,36),high=APTR.ReadUInt32(env,40),heads=APTR.ReadUInt32(env,12),track=APTR.ReadUInt32(env,20);
        if(high<low||heads==0||track==0) return false;
        Wide cylinder=Wide.Multiply(heads,track); if(cylinder.High!=0) return false;
        Wide length=Wide.Multiply(high-low+1,cylinder.Low),first=Wide.Multiply(low,cylinder.Low);
        if(length.High!=0||length.Low<4096||length.Low>65536||!Wide.Left(first,9,out start)) return false;
        blocks=length.Low; maximum=APTR.ReadUInt32(env,52); mask=APTR.ReadUInt32(env,56); return maximum>=512;
    }
    // Geometry and the mount's DMA class are immutable until the device closes.
    // Cache a direct-transfer capability only for masks with one contiguous
    // address range. The general transport handles holes, bouncing and splits.
    private const uint DirectIo=3072;
    private static void BindDirect(uint arena,APTR request,uint memoryType,Wide start,uint blocks,uint maximum,uint mask)
    {
        uint align=mask&(0u-mask),ceiling=mask|(align-1);
        if(mask==0||(ceiling&(ceiling+1))!=0||(memoryType&0xffffu&~(uint)Exec.MemoryFlags.Public)!=0||
            start.High!=0||blocks>(uint.MaxValue-start.Low)/512) return;
        APTR d=APTR.FromPointer(arena+DirectIo);
        APTR.WriteUInt32(d,0,request.Raw); APTR.WriteUInt32(d,4,ceiling); APTR.WriteUInt32(d,8,~mask|1u);
        APTR.WriteUInt32(d,12,maximum); APTR.WriteUInt32(d,16,start.Low);
    }
    [AmigaLibrary("device",AmigaLibraryBasePolicy.CallerProvided)]
    [AmigaLvo(-30)]
    private static extern void BeginDirect([M68kRegister(M68kRegister.A6)] APTR device,
        [M68kRegister(M68kRegister.A1)] APTR request);
    private static StepResult ServiceIo(uint arena,out uint token,out uint actual)
    {
        APTR d=APTR.FromPointer(arena+DirectIo),s=APTR.FromPointer(arena+2048),io=APTR.FromPointer(arena+136);
        APTR request=APTR.FromPointer(APTR.ReadUInt32(d,0));
        uint kind=APTR.ReadUInt32(io,0),buffer=APTR.ReadUInt32(io,12),bytes=APTR.ReadUInt32(io,16);
        // Issue already proved the relative sector range, buffer and length.
        // Any outstanding command, split prefix or stop belongs to the common transport.
        if(request.IsNull||APTR.ReadUInt32(s,0)!=0||APTR.ReadUInt32(s,48)!=0||APTR.ReadUInt32(s,64)!=0||
            (kind!=(uint)IoKind.Flush&&(bytes>APTR.ReadUInt32(d,12)||(buffer&APTR.ReadUInt32(d,8))!=0||
            buffer+bytes-1>APTR.ReadUInt32(d,4))))
            return NativeCpxTransport.ServiceEngineRequest(arena,out token,out actual,out _);
        token=APTR.ReadUInt32(io,20); bool flush=kind==(uint)IoKind.Flush;
        uint offset=flush?0:APTR.ReadUInt32(d,16)+(APTR.ReadUInt32(io,8)<<9);
        APTR.WriteUInt8(request,8,5); APTR.WriteUInt16(request,ExecLayout.IORequest.Command,(ushort)(flush?4:kind==(uint)IoKind.Write?25:24));
        APTR.WriteUInt8(request,ExecLayout.IORequest.Flags,1); APTR.WriteUInt8(request,ExecLayout.IORequest.Error,0); APTR.WriteUInt32(request,ExecLayout.IOStdReq.Actual,0);
        APTR.WriteUInt32(request,ExecLayout.IOStdReq.Length,bytes); APTR.WriteUInt32(request,ExecLayout.IOStdReq.Data,buffer); APTR.WriteUInt32(request,ExecLayout.IOStdReq.Offset,offset);
        BeginDirect(APTR.FromPointer(APTR.ReadUInt32(request,ExecLayout.IORequest.Device)),request);
        if((APTR.ReadUInt8(request,ExecLayout.IORequest.Flags)&1)!=0) {
            actual=flush?0:APTR.ReadUInt32(request,ExecLayout.IOStdReq.Actual);
            return APTR.ReadUInt8(request,ExecLayout.IORequest.Error)==0&&actual==bytes?StepResult.Complete:StepResult.Failed;
        }
        // Deferred device completion uses the normal token, abort and WaitIO path.
        APTR.WriteUInt32(s,4,bytes); APTR.WriteUInt32(s,8,token); APTR.WriteUInt32(s,12,flush?1u:0u);
        APTR.WriteUInt32(s,48,token); APTR.WriteUInt32(s,52,0); APTR.WriteUInt32(s,56,bytes);
        APTR.WriteUInt32(s,60,0); APTR.WriteUInt32(s,0,1); actual=0; return StepResult.Pending;
    }
    private static bool Drive(uint arena,APTR ioPort,APTR task,ref NovaEngine<NativeCpxMemory> engine)
    {
        uint signal=1u<<APTR.ReadUInt8(ioPort,15);
        while(true)
        {
            if((APTR.ReadUInt32(task,ExecLayout.Task.SignalReceived)&4096)!=0)
            { Exec.SetSignal(0,4096); engine.Cancel(); NativeCpxTransport.RequestStop(arena); }
            StepResult step=engine.RunBatch();
            if(step==StepResult.Complete) return engine.DosError==0;
            if(step==StepResult.Failed) return false;
            if(step!=StepResult.NeedIo) continue;
            StepResult transfer=ServiceIo(arena,out uint token,out uint actual);
            if(transfer is StepResult.Complete or StepResult.Failed) engine.CompleteIo(token,actual,transfer==StepResult.Complete);
            else if(NativeCpxTransport.Waiting(arena)&&APTR.ReadUInt32(ioPort,20)==ioPort.Raw+24)
            {
                uint woken=Exec.Wait(signal|4096);
                if((woken&4096)!=0) { engine.Cancel(); NativeCpxTransport.RequestStop(arena); }
            }
        }
    }
    private static void Run(uint arena,APTR port,APTR ioPort,APTR task,APTR node,ref NovaEngine<NativeCpxMemory> engine)
    {
        while(true)
        {
            Exec.WaitPort(port); APTR message=APTR.FromPointer(Exec.GetMsg(port));
            if(message.IsNull) continue;
            APTR packet=APTR.FromPointer(APTR.ReadUInt32(message,10)); if(packet.IsNull) continue;
            if(engine.BeginPacket(packet.Raw)!=CpxError.None) { Reply(message,packet,port,0,202); continue; }
            bool succeeded=Drive(arena,ioPort,task,ref engine);
            bool shutdown=APTR.ReadUInt32(packet,8)==5&&succeeded;
            if(shutdown&&node.IsNotNull) APTR.WriteUInt32(node,8,0);
            APTR reply=APTR.FromPointer(APTR.ReadUInt32(packet,4));
            APTR.WriteUInt32(packet,4,port.Raw); Exec.PutMsg(reply,message);
            if(shutdown) return;
        }
    }
    private static void Reply(APTR message,APTR packet,APTR port,uint result,uint error)
    {
        APTR reply=APTR.FromPointer(APTR.ReadUInt32(packet,4));
        APTR.WriteUInt32(packet,12,result); APTR.WriteUInt32(packet,16,error); APTR.WriteUInt32(packet,4,port.Raw);
        Exec.PutMsg(reply,message);
    }
}
