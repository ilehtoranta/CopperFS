using Amiga;
namespace CopperCPX.InstructionTests;

public static partial class CpxBootProbe
{
    private static APTR Receive(APTR port)
    {
        for(uint n=0;n<1000;n++) { APTR message=APTR.FromPointer(Exec.GetMsg(port)); if(message.IsNotNull) return message; DOS.Delay(1); }
        return APTR.Null;
    }
    private static void SendRecord(APTR storage,APTR port,APTR replyPort,uint handle,uint ticks)
    {
        for(int n=0;n<80;n+=4) APTR.WriteUInt32(storage,n,0);
        APTR packet=APTR.FromPointer(storage.Raw+24);
        APTR.WriteUInt32(storage,10,packet.Raw); APTR.WriteUInt16(storage,18,80);
        APTR.WriteUInt32(packet,0,storage.Raw); APTR.WriteUInt32(packet,8,2008);
        APTR.WriteUInt32(packet,12,0x11223344); APTR.WriteUInt32(packet,20,handle); APTR.WriteUInt32(packet,28,8); APTR.WriteUInt32(packet,36,ticks);
        DOS.SendPkt(packet,port,replyPort);
    }
    private static uint Records(APTR port,APTR data)
    {
        APTR control=APTR.FromPointer(data.Raw-512); APTR.WriteUInt32(control,28,170);
        APTR replies=Exec.CreateMsgPort(); if(replies.IsNull) return 170;
        var first=DOS.Open("CPX0:Payload",DOS.FileMode.ReadWrite); var second=DOS.Open("CPX0:Payload",DOS.FileMode.ReadWrite);
        if(!first.HasValue||!second.HasValue) return 171;
        uint a=APTR.ReadUInt32(APTR.FromPointer(first.Value.Raw<<2),36),b=APTR.ReadUInt32(APTR.FromPointer(second.Value.Raw<<2),36);
        APTR.WriteUInt32(control,0,a-(CpxArena.Scratch+6*1024+CpxArena.IndexBytes+8)); APTR.WriteUInt32(control,28,172);
        // Full grant pool, cooperative ranges beyond EOF, then retain one lock.
        for(uint n=0;n<32;n++) if(DOS.LockRecord(first.Value,n*16,8,1,0)==0) return 172;
        if(DOS.LockRecord(first.Value,4096,8,1,0)!=0||(uint)DOS.IoErr()!=103) return 173;
        for(uint n=1;n<32;n++) if(DOS.UnLockRecord(first.Value,n*16,8)==0) return 174;
        APTR.WriteUInt32(control,28,175);
        if(DOS.LockRecord(second.Value,0,8,1,0)!=0||(uint)DOS.IoErr()!=241) return 175;
        APTR.WriteUInt32(control,28,176);
        if(DOS.LockRecord(second.Value,0,8,0,2)!=0||(uint)DOS.IoErr()!=242) return 176;
        APTR.WriteUInt32(control,28,177);
        SendRecord(data,port,replies,b,5000);
        if(DOS.DoPkt(port,1027,0,0,0,0,0)==0||Exec.GetMsg(replies)!=0||APTR.ReadUInt32(data,36)!=0x11223344) return 177;
        APTR.WriteUInt32(control,28,178);
        if(DOS.UnLockRecord(first.Value,0,8)==0) return 178;
        APTR.WriteUInt32(control,28,179);
        if(Receive(replies).Raw!=data.Raw||APTR.ReadUInt32(data,36)!=uint.MaxValue||APTR.ReadUInt32(data,40)!=0) return 179;
        if(DOS.UnLockRecord(second.Value,0,8)==0||DOS.LockRecord(first.Value,0,8,1,0)==0) return 180;
        // Close cancels its parked operation, while another handle remains usable.
        SendRecord(data,port,replies,b,5000);
        APTR.WriteUInt32(control,28,181);
        if(DOS.Close(second.Value)==0||Receive(replies).Raw!=data.Raw||APTR.ReadUInt32(data,40)!=211) return 181;
        second=DOS.Open("CPX0:Payload",DOS.FileMode.ReadWrite); if(!second.HasValue) return 182;
        b=APTR.ReadUInt32(APTR.FromPointer(second.Value.Raw<<2),36);
        // Exhaust all 16 waiters and all 16 preallocated native timers. Ctrl-C
        // cancels only parked record requests; it is not a new DOS packet ABI.
        for(uint n=0;n<16;n++) SendRecord(APTR.FromPointer(data.Raw+n*80),port,replies,b,5000);
        APTR.WriteUInt32(control,28,183);
        if(DOS.LockRecord(second.Value,0,8,0,5000)!=0||(uint)DOS.IoErr()!=103) return 183;
        APTR.WriteUInt32(control,28,184);
        Exec.Signal(APTR.FromPointer(APTR.ReadUInt32(port,16)),4096);
        for(uint n=0;n<16;n++)
        {
            APTR message=Receive(replies); if(message.IsNull) return 184;
            uint address=message.Raw; if(address<data.Raw||address>=data.Raw+1280) return 185;
            APTR packet=APTR.FromPointer(APTR.ReadUInt32(message,10));
            if(APTR.ReadUInt32(packet,12)!=0||APTR.ReadUInt32(packet,16)!=304||APTR.ReadUInt32(packet,44)!=0) return 186;
            APTR.WriteUInt32(packet,44,1);
        }
        if(Exec.GetMsg(replies)!=0||DOS.UnLockRecord(first.Value,0,8)==0) return 187;
        // SFS2 wide record ABI uses pointers to paired unsigned values.
        APTR.WriteUInt32(data,0,1); APTR.WriteUInt32(data,4,0xfffffff0); APTR.WriteUInt32(data,8,0); APTR.WriteUInt32(data,12,32);
        APTR.WriteUInt32(control,28,188);
        if(DOS.DoPkt(port,26402,(int)a,(int)data.Raw,(int)data.Raw+8,1,0)==0) return 188;
        if(DOS.DoPkt(port,26402,(int)b,(int)data.Raw,(int)data.Raw+8,1,0)!=0||(uint)DOS.IoErr()!=241) return 189;
        if(DOS.DoPkt(port,26403,(int)a,(int)data.Raw,(int)data.Raw+8,0,0)==0) return 190;
        if(DOS.Close(first.Value)==0||DOS.Close(second.Value)==0) return 191;
        Exec.DeleteMsgPort(replies); return 0;
    }
    private static void NotifyRequest(APTR request,APTR target,uint flags,uint name,uint signal=0)
    {
        for(int n=0;n<48;n+=4) APTR.WriteUInt32(request,n,0);
        APTR.WriteUInt32(request,0,name); APTR.WriteUInt32(request,12,flags); APTR.WriteUInt32(request,16,target.Raw); APTR.WriteUInt8(request,20,(byte)signal);
    }
    private static bool ReceiveSignal(uint mask)
    { for(uint n=0;n<1000;n++) { if((Exec.SetSignal(0,mask)&mask)!=0) return true; DOS.Delay(1); } return false; }
    private static uint Notifications(APTR port,APTR data)
    {
        APTR control=APTR.FromPointer(data.Raw-512); APTR.WriteUInt32(control,28,300);
        APTR messages=Exec.CreateMsgPort(); if(messages.IsNull) return 300;
        uint name=CString.ToUInt32(CString.FromLiteral("CPX0:Inline"));
        NotifyRequest(data,messages,25,name);
        if(DOS.StartNotify(data)==0) return 301;
        APTR.WriteUInt32(control,28,302);
        APTR initial=Receive(messages);
        if(initial.IsNull||APTR.ReadUInt32(initial,20)!=0x40000000||APTR.ReadUInt16(initial,24)!=0x1234||APTR.ReadUInt32(initial,26)!=data.Raw) return 302;
        var file=DOS.Open("CPX0:Inline",DOS.FileMode.ReadWrite); if(!file.HasValue) return 303;
        APTR bytes=APTR.FromPointer(data.Raw+512); APTR.WriteUInt8(bytes,0,7);
        if(DOS.Write(file.Value,bytes,1)!=1||Exec.GetMsg(messages)!=0||APTR.ReadUInt32(data,40)!=1) return 304;
        if(DOS.Close(file.Value)==0||DOS.SetProtection("CPX0:Inline",0)==0) return 305;
        if(Exec.GetMsg(messages)!=0||APTR.ReadUInt32(data,40)!=1) return 306;
        Exec.ReplyMsg(initial); APTR held=Receive(messages); if(held.IsNull||APTR.ReadUInt32(data,40)!=1) return 307;
        APTR.WriteUInt32(control,28,308);
        DOS.EndNotify(data); if(APTR.ReadUInt32(data,40)!=0) return 308;
        // The application can release its request immediately after EndNotify.
        // A held message still owns the fixed watch slot until ReplyMsg returns.
        for(int n=0;n<48;n++) APTR.WriteUInt8(data,n,0xa5);
        Exec.ReplyMsg(held); if(DOS.DoPkt(port,1027,0,0,0,0,0)==0) return 309;
        for(int n=0;n<48;n++) if(APTR.ReadUInt8(data,n)!=0xa5) return 310;
        NotifyRequest(data,messages,25,name); if(DOS.StartNotify(data)==0) return 311;
        APTR.WriteUInt32(control,28,312);
        DOS.Delay(2); DOS.EndNotify(data); if(Exec.GetMsg(messages)!=0||APTR.ReadUInt32(data,40)!=0) return 312;
        sbyte signal=Exec.AllocSignal(-1); if(signal<0) return 313; uint mask=1u<<signal;
        APTR task=Exec.FindTask(CString.FromPointer(0)); Exec.SetSignal(0,mask);
        NotifyRequest(data,task,18,CString.ToUInt32(CString.FromLiteral("CPX0:NotifyTemp")),(uint)signal);
        if(DOS.StartNotify(data)==0||(Exec.SetSignal(0,mask)&mask)!=0) return 314;
        APTR.WriteUInt32(control,28,315);
        var created=DOS.CreateDir("CPX0:NotifyTemp"); if(!created.HasValue) return 315;
        DOS.UnLock(created.Value); if(!ReceiveSignal(mask)) return 316;
        if(DOS.DeleteFile("CPX0:NotifyTemp")==0||!ReceiveSignal(mask)) return 317;
        DOS.EndNotify(data); Exec.FreeSignal(signal); Exec.DeleteMsgPort(messages); return 0;
    }
}
