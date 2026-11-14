using Amiga;
namespace CopperCPX.InstructionTests;

// Workload/bootstrap pattern follows the repository's MIT-licensed
// CorrectnessProbe. This separate probe fixes RAM in the host and stack at 4096.
public static partial class CpxBootProbe
{
    public static int Entry()
    {
        APTR dos=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),37); if(dos.IsNull) return 20; DOS.DOSLibraryBase=dos;
        APTR process=Exec.FindTask(CString.FromPointer(0)); APTR.WriteUInt32(process,184,uint.MaxValue);
        APTR control=APTR.FromPointer(Exec.AllocMem(2048,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear)); if(control.IsNull) { Report(250); return 1; }
        Report(200); Report((int)(control.Raw>>24)); Report((int)(control.Raw>>16)); Report((int)(control.Raw>>8)); Report((int)control.Raw);
        APTR.WriteUInt32(control,40,Exec.AvailMem(Exec.MemoryFlags.Public));
        var segment=DOS.LoadSeg("HANDLER"); if(!segment.HasValue) return Fail(control,1);
        APTR.WriteUInt32(control,56,Exec.AvailMem(Exec.MemoryFlags.Public));
        APTR.WriteUInt32(control,32,(segment.Value.Raw<<2)+4); Report(224);
        APTR node=DOS.MakeDosEntry(CString.FromLiteral("CPX0"),(int)DosListType.Device); if(node.IsNull) return Fail(control,2);
        APTR startup=APTR.FromPointer(control.Raw+64),name=APTR.FromPointer(control.Raw+96),env=APTR.FromPointer(control.Raw+128);
        var device=CString.FromLiteral("copperhdf.device"); APTR.WriteUInt8(name,0,16);
        for(int n=0;n<16;n++) APTR.WriteUInt8(name,n+1,APTR.ReadUInt8(APTR.FromPointer(CString.ToUInt32(device)),n));
        APTR.WriteUInt32(startup,4,name.Raw>>2); APTR.WriteUInt32(startup,8,env.Raw>>2);
        APTR.WriteUInt32(env,0,19); APTR.WriteUInt32(env,4,128); APTR.WriteUInt32(env,12,2); APTR.WriteUInt32(env,16,1);
        APTR.WriteUInt32(env,20,128); APTR.WriteUInt32(env,40,31); APTR.WriteUInt32(env,48,1);
        APTR.WriteUInt32(env,52,65536); APTR.WriteUInt32(env,56,0xfffffffe); APTR.WriteUInt32(env,64,0x43505834);
        if((APTR.ReadUInt32(control,60)&64)!=0)
        {
            APTR.WriteUInt32(env,48,(uint)(Exec.MemoryFlags.Public|Exec.MemoryFlags.Chip));
            APTR.WriteUInt32(env,56,0x0007f000); // chip address ceiling and a 4096-byte buffer alignment
        }
        APTR.WriteUInt32(node,DosLayout.DeviceNode.StackSize,4096); APTR.WriteUInt32(node,DosLayout.DeviceNode.Priority,10);
        APTR.WriteUInt32(node,DosLayout.DeviceNode.Startup,startup.Raw>>2); APTR.WriteUInt32(node,DosLayout.DeviceNode.SegmentList,segment.Value.Raw);
        APTR.WriteUInt32(node,DosLayout.DeviceNode.GlobalVector,uint.MaxValue);
        if(DOS.AddDosEntry(node)==0) return Fail(control,3);
        APTR port=DOS.DeviceProc("CPX0:"); if(port.IsNull) return Fail(control,4);
        APTR.WriteUInt32(control,44,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(225);
        APTR data=APTR.FromPointer(control.Raw+512);
        if((APTR.ReadUInt32(control,60)&32)!=0) return BackupAnchorWork(control,port,data);
        if((APTR.ReadUInt32(control,60)&24)==16) return OwnerWork(control,port,data);
        if((APTR.ReadUInt32(control,60)&8)!=0) return FormatWork(control,port,data);
        if((APTR.ReadUInt32(control,60)&4)!=0) return FinishAsync(control,port,data);
        var file=DOS.Open("CPX0:Inline",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,5);
        if(!CheckUnsupportedPackets(control,port,APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36),209)) return Fail(control,960);
        int recovered=DOS.Read(file.Value,data,3);
        if((APTR.ReadUInt32(control,60)&2)!=0)
        { if(recovered!=3||APTR.ReadUInt8(data,0)!=114||APTR.ReadUInt8(data,1)!=112||APTR.ReadUInt8(data,2)!=108) return Fail(control,33); }
        else if(recovered!=0) return Fail(control,34);
        if(DOS.Seek(file.Value,0,-1)!=recovered) return Fail(control,35);
        for(int n=0;n<80;n++) APTR.WriteUInt8(data,n,(byte)(n*3+7));
        if(DOS.Write(file.Value,data,80)!=80) return Fail(control,6);
        if(DOS.Seek(file.Value,0,-1)!=80) return Fail(control,7);
        for(int n=0;n<80;n++) APTR.WriteUInt8(data,n,0);
        if(DOS.Read(file.Value,data,80)!=80) return Fail(control,8);
        for(int n=0;n<80;n++) if(APTR.ReadUInt8(data,n)!=(byte)(n*3+7)) return Fail(control,9);
        if(DOS.Close(file.Value)==0) return Fail(control,10);
        var locked=DOS.Lock("CPX0:Inline",DOS.LockMode.Shared); if(!locked.HasValue) return Fail(control,17);
        DOS.UnLock(locked.Value);
        if(DOS.SetProtection("CPX0:Inline",4)==0) return Fail(control,18);
        if(DOS.Open("CPX0:Inline",DOS.FileMode.ReadWrite).HasValue) return Fail(control,19);
        if(DOS.SetProtection("CPX0:Inline",0)==0) return Fail(control,20);
        file=DOS.Open("CPX0:Inline",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,21);
        if(DOS.SetFileSize(file.Value,40,-1)!=40) return Fail(control,22);
        if(DOS.Close(file.Value)==0) return Fail(control,23);
        file=DOS.Open("CPX0:Payload",DOS.FileMode.OldFile); if(!file.HasValue) return Fail(control,11);
        if(DOS.Read(file.Value,data,1024)!=1024) return Fail(control,12);
        for(int n=0;n<1024;n++) if(APTR.ReadUInt8(data,n)!=(byte)(n*7+11)) return Fail(control,13);
        if(DOS.Close(file.Value)==0) return Fail(control,14);
        file=DOS.Open("CPX0:Wide",DOS.FileMode.OldFile); if(!file.HasValue) return Fail(control,28);
        uint internalHandle=APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36);
        APTR.WriteUInt32(data,0,1); APTR.WriteUInt32(data,4,9);
        if(DOS.DoPkt(port,26400,(int)internalHandle,(int)data.Raw,-1,(int)data.Raw+8,0)==0) return Fail(control,29);
        if(APTR.ReadUInt32(data,8)!=0||APTR.ReadUInt32(data,12)!=0) return Fail(control,30);
        if(DOS.Read(file.Value,data,1)!=1||APTR.ReadUInt8(data,0)!=0x5a) return Fail(control,31);
        if(DOS.Close(file.Value)==0) return Fail(control,32);
        for(int n=0;n<1024;n++) APTR.WriteUInt8(data,n,(byte)(n*7+11));
        if((APTR.ReadUInt32(control,60)&1)==0)
        {
            file=DOS.Open("CPX0:Inline",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,24);
            if(DOS.Seek(file.Value,0,1)!=0) return Fail(control,25);
            if(DOS.Write(file.Value,data,1024)!=1024) return Fail(control,26);
            if(DOS.Close(file.Value)==0) return Fail(control,27);
        }
        // Exercise namespace splits, parent changes, pool ownership, external
        // truncation/zero extension and replacement-open on the actual handler.
        var directory=DOS.CreateDir("CPX0:Created"); if(!directory.HasValue) return Fail(control,40); DOS.UnLock(directory.Value);
        APTR generated=APTR.FromPointer(control.Raw+1600); var pattern=CString.FromLiteral("CPX0:New00");
        for(int n=0;n<11;n++) APTR.WriteUInt8(generated,n,APTR.ReadUInt8(APTR.FromPointer(CString.ToUInt32(pattern)),n));
        for(int n=0;n<8;n++)
        {
            APTR.WriteUInt8(generated,8,(byte)(48+n/10)); APTR.WriteUInt8(generated,9,(byte)(48+n%10));
            file=DOS.Open(CString.FromPointer(generated.Raw),DOS.FileMode.NewFile); if(!file.HasValue) return Fail(control,41);
            if(DOS.Close(file.Value)==0) return Fail(control,42);
        }
        if(DOS.Rename("CPX0:New00","CPX0:Created/Moved")==0) return Fail(control,43);
        file=DOS.Open("CPX0:Created/Moved",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,44);
        for(int n=0;n<1024;n++) APTR.WriteUInt8(data,n,0x5a);
        if(DOS.Write(file.Value,data,1024)!=1024) return Fail(control,45);
        if(DOS.DeleteFile("CPX0:Created/Moved")!=0||(uint)DOS.IoErr()!=202) return Fail(control,46);
        if(DOS.SetFileSize(file.Value,701,-1)!=701) return Fail(control,47);
        if(DOS.SetFileSize(file.Value,2048,-1)!=2048) return Fail(control,48);
        if(DOS.Seek(file.Value,701,-1)<0||DOS.Read(file.Value,data,1024)!=1024) return Fail(control,49);
        for(int n=0;n<1024;n++) if(APTR.ReadUInt8(data,n)!=0) return Fail(control,50);
        if(DOS.Close(file.Value)==0) return Fail(control,51);
        file=DOS.Open("CPX0:Created/Moved",DOS.FileMode.NewFile); if(!file.HasValue) return Fail(control,52);
        if(DOS.Seek(file.Value,0,1)!=0||DOS.Write(file.Value,data,3)!=3||DOS.Close(file.Value)==0) return Fail(control,53);
        if(DOS.DeleteFile("CPX0:Created/Moved")==0||DOS.DeleteFile("CPX0:Created")==0) return Fail(control,54);
        for(int n=1;n<8;n++)
        {
            APTR.WriteUInt8(generated,8,(byte)(48+n/10)); APTR.WriteUInt8(generated,9,(byte)(48+n%10));
            if(DOS.DeleteFile(CString.FromPointer(generated.Raw))==0) return Fail(control,55);
        }
        file=DOS.Open("CPX0:Extents",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,60);
        for(int n=0;n<17;n++) APTR.WriteUInt8(data,n,0x7c);
        if(DOS.Seek(file.Value,6157,-1)<0||DOS.Write(file.Value,data,17)!=17) return Fail(control,61);
        if(DOS.Seek(file.Value,7168,-1)<0||DOS.Write(file.Value,data,17)!=17) return Fail(control,62);
        if(DOS.Seek(file.Value,8192,-1)<0||DOS.Write(file.Value,data,17)!=17) return Fail(control,63);
        if(DOS.Close(file.Value)==0) return Fail(control,64);
        file=DOS.Open("CPX0:Unwritten",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,65);
        if(DOS.Seek(file.Value,39*4096+525,-1)<0||DOS.Write(file.Value,data,17)!=17) return Fail(control,66);
        if(DOS.Seek(file.Value,39*4096+512,-1)<0||DOS.Read(file.Value,data,512)!=512) return Fail(control,67);
        for(int n=0;n<512;n++) if(APTR.ReadUInt8(data,n)!=(n>=13&&n<30?0x7c:0)) return Fail(control,68);
        if(DOS.Close(file.Value)==0) return Fail(control,69);
        file=DOS.Open("CPX0:EmptyTree",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,70);
        for(int n=0;n<17;n++) APTR.WriteUInt8(data,n,0x7c);
        if(DOS.Write(file.Value,data,17)!=17||DOS.Close(file.Value)==0) return Fail(control,71);
        uint infoFailure=Information(control,port,data); if(infoFailure!=0) return Fail(control,infoFailure);
        uint lifecycleFailure=Lifecycle(port,data); if(lifecycleFailure!=0) return Fail(control,lifecycleFailure);
        uint libraryFailure=LifecycleLibrary(data); if(libraryFailure!=0) return Fail(control,libraryFailure);
        uint linkFailure=Links(port,data); if(linkFailure!=0) return Fail(control,linkFailure);
        return FinishAsync(control,port,data);
    }
    private static int FinishAsync(APTR control,APTR port,APTR data)
    {
        APTR.WriteUInt32(control,36,170);
        uint recordFailure=Records(port,data); if(recordFailure!=0) return Fail(control,recordFailure);
        APTR.WriteUInt32(control,36,300);
        uint notifyFailure=Notifications(port,data); if(notifyFailure!=0) return Fail(control,notifyFailure);
        if((APTR.ReadUInt32(control,60)&4)==0)
        {
            APTR.WriteUInt32(control,36,500);
            uint exAllFailure=ExAllWork(port,data); if(exAllFailure!=0) return Fail(control,exAllFailure);
        }
        APTR.WriteUInt32(control,36,400);
        uint mediaFailure=MediaLifecycle(control,port,data); if(mediaFailure!=0) return Fail(control,mediaFailure);
        APTR.WriteUInt32(control,36,0);
        if(DOS.DoPkt(port,27,0,0,0,0,0)==0) return Fail(control,15);
        APTR.WriteUInt32(control,48,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(226);
        if(DOS.DoPkt(port,5,0,0,0,0,0)==0) return Fail(control,16);
        DOS.Delay(5); APTR.WriteUInt32(control,52,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(210); return 0;
    }
    private static int BackupAnchorWork(APTR control,APTR port,APTR data)
    {
        if(DOS.DoPkt(port,25,(int)(data.Raw>>2),0,0,0,0)==0||APTR.ReadUInt32(data,20)!=4096) return Fail(control,970);
        var file=DOS.Open("CPX0:Rescued",DOS.FileMode.ReadWrite); if(!file.HasValue) return Fail(control,971);
        uint handle=APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36);
        if((APTR.ReadUInt32(control,60)&64)!=0)
        {
            // The first handle occupies pool slot zero. Snapshot while mounted.
            uint arena=handle-(CpxArena.Scratch+6*4096+CpxArena.IndexBytes+8);
            APTR transport=APTR.FromPointer(arena+2048);
            APTR.WriteUInt32(control,1880,APTR.ReadUInt32(transport,84));
            APTR.WriteUInt32(control,1884,APTR.ReadUInt32(transport,76));
        }
        if(!CheckUnsupportedPackets(control,port,handle,209)) return Fail(control,972);
        if(DOS.Read(file.Value,data,4)!=4||APTR.ReadUInt32(data,0)!=0x6f6c6421||DOS.Seek(file.Value,0,-1)!=4) return Fail(control,973);
        APTR.WriteUInt32(data,0,0x6e657721);
        if(DOS.Write(file.Value,data,4)!=4||DOS.Close(file.Value)==0||DOS.DoPkt(port,27,0,0,0,0,0)==0) return Fail(control,974);
        APTR.WriteUInt32(control,48,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(226);
        if(DOS.DoPkt(port,5,0,0,0,0,0)==0) return Fail(control,975);
        DOS.Delay(5); APTR.WriteUInt32(control,52,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(210); return 0;
    }
    private static bool CheckUnsupportedPackets(APTR control,APTR port,uint handle,int expectedError)
    {
        uint previous=APTR.ReadUInt32(control,36);
        for(int index=0;index<3;index++)
        {
            int action=index==0?20:index==1?30:33;
            APTR.WriteUInt32(control,36,(uint)(960+index));
            int result=DOS.DoPkt(port,action,(int)handle,50000,0,0,0);
            if(result!=0||(int)DOS.IoErr()!=expectedError) return false;
        }
        APTR.WriteUInt32(control,36,previous);
        return true;
    }
    private static uint Information(APTR control,APTR port,APTR fib)
    {
        if(DOS.SetComment("CPX0:Inline","native comment")==0) return 98;
        var root=DOS.Lock("CPX0:",DOS.LockMode.Shared); if(!root.HasValue) return 80;
        if(DOS.Examine(root.Value,fib)==0||APTR.ReadUInt8(fib,8)!=67) return 81;
        uint count=0,aliases=0,latin=0,comment=0;
        while(DOS.ExNext(root.Value,fib)!=0)
        {
            if(++count>12) return 82;
            if(APTR.ReadUInt8(fib,8)==95&&APTR.ReadUInt8(fib,9)==95&&APTR.ReadUInt8(fib,10)==117) aliases++;
            if(APTR.ReadUInt8(fib,8)==0xe9)
            { if(APTR.ReadUInt8(fib,9)!=0xc4||APTR.ReadUInt8(fib,144)!=0xc0||APTR.ReadUInt8(fib,145)!=0xff) return 83; latin++; }
            if(APTR.ReadUInt8(fib,8)==73) { if(APTR.ReadUInt8(fib,144)!=110||APTR.ReadUInt8(fib,157)!=116) return 99; comment++; }
        }
        if((uint)DOS.IoErr()!=232||count!=9||aliases!=2||latin!=1||comment!=1) return 84;
        if(InfoPacket(port,26408,root.Value.Raw,fib.Raw>>2,0)==0||APTR.ReadUInt8(fib,8)!=10) return 85;
        count=0;
        while(InfoPacket(port,26409,root.Value.Raw,fib.Raw>>2,0)!=0) if(++count>12) return 86;
        if(count!=9||(uint)DOS.IoErr()!=232) return 87;
        APTR info=APTR.FromPointer(fib.Raw+300),wide=APTR.FromPointer(fib.Raw+360);
        if(InfoPacket(port,26,root.Value.Raw,info.Raw>>2,0)==0||APTR.ReadUInt32(info,12)!=8192||APTR.ReadUInt32(info,20)!=512) return 88;
        if(InfoPacket(port,25,info.Raw>>2,0,0)==0||APTR.ReadUInt32(info,24)!=0x43505834) return 89;
        if(InfoPacket(port,26407,6,wide.Raw,8)==0||APTR.ReadUInt32(wide,0)!=0||APTR.ReadUInt32(wide,4)!=8192) return 90;
        if(InfoPacket(port,26407,7,wide.Raw,8)==0||APTR.ReadUInt32(wide,4)!=APTR.ReadUInt32(info,16)) return 91;
        DOS.UnLock(root.Value);
        var file=DOS.Open("CPX0:Wide",DOS.FileMode.OldFile); if(!file.HasValue) return 92;
        uint handle=APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36);
        if(InfoPacket(port,26410,handle,fib.Raw>>2,0)==0||APTR.ReadUInt32(fib,228)!=1||APTR.ReadUInt32(fib,232)!=10) return 93;
        if(DOS.Close(file.Value)==0) return 94;
        if(DOS.DoPkt(port,24,0,0,0,0,0)!=0||(uint)DOS.IoErr()!=115) return 100;
        if(InfoPacket(port,26407,2,wide.Raw,4)!=0||(uint)DOS.IoErr()!=120) return 101;
        var empty=DOS.CreateDir("CPX0:EnumEmpty"); if(!empty.HasValue) return 95;
        if(DOS.Examine(empty.Value,fib)==0||DOS.ExNext(empty.Value,fib)!=0||(uint)DOS.IoErr()!=232) return 96;
        DOS.UnLock(empty.Value); if(DOS.DeleteFile("CPX0:EnumEmpty")==0) return 97;
        return 0;
    }
    private static int InfoPacket(APTR port,int action,uint a,uint b,uint c)=>DOS.DoPkt(port,action,(int)a,(int)b,(int)c,0,0);
    private static uint Lifecycle(APTR port,APTR data)
    {
        var file=DOS.Open("CPX0:Inline",DOS.FileMode.ReadWrite); if(!file.HasValue) return 110;
        uint handle=APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36);
        int locked=DOS.DoPkt(port,1030,(int)handle,0,0,0,0); if(locked==0) return 111;
        if(DOS.DoPkt(port,1028,1,(int)file.Value.Raw,-1,0,0)!=0||(uint)DOS.IoErr()!=202) return 112;
        if(DOS.DoPkt(port,15,locked,0,0,0,0)==0) return 113;
        if(DOS.DoPkt(port,1028,1,(int)file.Value.Raw,-1,0,0)==0) return 114;
        if(DOS.DoPkt(port,1030,(int)handle,0,0,0,0)!=0||(uint)DOS.IoErr()!=202) return 115;
        int parent=DOS.DoPkt(port,1031,(int)handle,0,0,0,0); if(parent==0) return 116;
        if(DOS.DoPkt(port,29,parent,0,0,0,0)!=0||(uint)DOS.IoErr()!=0) return 117;
        if(DOS.DoPkt(port,15,parent,0,0,0,0)==0||DOS.Close(file.Value)==0) return 118;
        var source=DOS.Lock("CPX0:Inline",DOS.LockMode.Shared); if(!source.HasValue) return 119;
        var duplicate=DOS.Lock("CPX0:Inline",DOS.LockMode.Shared); if(!duplicate.HasValue) return 127;
        var other=DOS.Lock("CPX0:Payload",DOS.LockMode.Shared); if(!other.HasValue) return 128;
        if(DOS.SameLock(source.Value,duplicate.Value)!=0||DOS.SameLock(source.Value,other.Value)!=1) return 129;
        DOS.UnLock(duplicate.Value); DOS.UnLock(other.Value);
        // A caller-supplied FileHandle is the actual packet ABI; no DOS allocation
        // is attributed to the handler's fixed pools.
        for(int n=0;n<44;n+=4) APTR.WriteUInt32(data,n,0);
        if(DOS.DoPkt(port,1026,(int)(data.Raw>>2),(int)source.Value.Raw,0,0,0)==0) return 120;
        handle=APTR.ReadUInt32(data,36); if(handle==0||DOS.DoPkt(port,1007,(int)handle,0,0,0,0)==0) return 121;
        if(DOS.DoPkt(port,1023,1,123,0,0,0)==0) return 122;
        if(DOS.SetComment("CPX0:Inline","blocked")!=0||(uint)DOS.IoErr()!=214) return 123;
        if(DOS.DoPkt(port,1023,0,456,0,0,0)!=0||(uint)DOS.IoErr()!=210) return 124;
        if(DOS.DoPkt(port,1023,0,123,0,0,0)==0) return 125;
        if(DOS.DoPkt(port,18,100,0,0,0,0)!=0||(uint)DOS.IoErr()!=0) return 126;
        return 0;
    }
    private static uint LifecycleLibrary(APTR data)
    {
        var source=DOS.Lock("CPX0:Payload",DOS.LockMode.Shared); if(!source.HasValue) return 150;
        if(DOS.ChangeMode(DosChangeModeTarget.Lock,source.Value,DOS.LockMode.Exclusive)==0||
           DOS.ChangeMode(DosChangeModeTarget.Lock,source.Value,DOS.LockMode.Shared)==0) return 151;
        var file=DOS.OpenFromLock(source.Value); if(!file.HasValue) return 152;
        // OpenFromLock consumes the lock only on success; DOS owns the new FH.
        if(DOS.Read(file.Value,data,1)!=1||APTR.ReadUInt8(data,0)!=11) return 153;
        var duplicate=DOS.DupLockFromFH(file.Value); if(!duplicate.HasValue) return 154;
        if(DOS.ChangeMode(DosChangeModeTarget.FileHandle,file.Value,DOS.LockMode.Exclusive)!=0||(uint)DOS.IoErr()!=202) return 155;
        var parent=DOS.ParentDir(duplicate.Value); if(!parent.HasValue) return 156;
        var fromFile=DOS.ParentOfFH(file.Value); if(!fromFile.HasValue||DOS.SameLock(parent.Value,fromFile.Value)!=0) return 157;
        DOS.UnLock(duplicate.Value);
        if(DOS.ChangeMode(DosChangeModeTarget.FileHandle,file.Value,DOS.LockMode.Exclusive)==0||
           DOS.ChangeMode(DosChangeModeTarget.FileHandle,file.Value,DOS.LockMode.Shared)==0) return 158;
        if(DOS.Seek(file.Value,0,0)!=1||DOS.Read(file.Value,data,1)!=1||APTR.ReadUInt8(data,0)!=18) return 159;
        if(DOS.ParentDir(parent.Value).HasValue||(uint)DOS.IoErr()!=0) return 160;
        // Failed conversion of a directory must leave its source lock usable.
        if(DOS.OpenFromLock(parent.Value).HasValue||(uint)DOS.IoErr()!=212||DOS.Examine(parent.Value,data)==0) return 161;
        DOS.UnLock(parent.Value); DOS.UnLock(fromFile.Value);
        if(DOS.Close(file.Value)==0) return 162;
        return 0;
    }
    private static uint Links(APTR port,APTR data)
    {
        if(DOS.MakeLink("CPX0:Soft",(int)CString.ToUInt32(CString.FromLiteral("Inline")),1)==0) return 130;
        if(DOS.ReadLink(port,default,"Soft",data,512)!=6||APTR.ReadUInt8(data,0)!=73) return 131;
        var file=DOS.Open("CPX0:Soft",DOS.FileMode.OldFile); if(!file.HasValue) return 132;
        if(DOS.Close(file.Value)==0) return 133;
        var source=DOS.Lock("CPX0:Inline",DOS.LockMode.Shared); if(!source.HasValue) return 134;
        if(DOS.MakeLink("CPX0:Hard",(int)source.Value.Raw,0)==0) return 135;
        DOS.UnLock(source.Value); file=DOS.Open("CPX0:Hard",DOS.FileMode.OldFile); if(!file.HasValue) return 136;
        if(DOS.Close(file.Value)==0) return 137;
        if(DOS.MakeLink("CPX0:LoopA",(int)CString.ToUInt32(CString.FromLiteral("LoopB")),1)==0||
           DOS.MakeLink("CPX0:LoopB",(int)CString.ToUInt32(CString.FromLiteral("LoopA")),1)==0) return 138;
        if(DOS.Open("CPX0:LoopA",DOS.FileMode.OldFile).HasValue) return 139;
        if(DOS.DeleteFile("CPX0:Soft")==0||DOS.DeleteFile("CPX0:Hard")==0||DOS.DeleteFile("CPX0:LoopA")==0||DOS.DeleteFile("CPX0:LoopB")==0) return 140;
        return 0;
    }
    private static int Fail(APTR control,uint stage)
    { APTR.WriteUInt32(control,20,stage); APTR.WriteUInt32(control,24,(uint)DOS.IoErr()); Report(250); return (int)stage; }
    private static void Report(int b)
    { APTR.WriteUInt8(APTR.FromPointer(0xbfe301),0,255); APTR.WriteUInt8(APTR.FromPointer(0xbfe101),0,(byte)b); }
}
