using Amiga;
namespace CopperCPX.InstructionTests;

public static partial class CpxBootProbe
{
    private static int JournalCrashWork(APTR control,APTR data)
    {
        uint flags=APTR.ReadUInt32(control,60);
        uint operation=flags>>12;
        if(operation!=0)
        {
            if((flags&512)==0)
            {
                if(operation==4)
                {
                    var handle=DOS.Open("CPX0:target",DOS.FileMode.ReadWrite);
                    if(!handle.HasValue) return Fail(control,1110);
                    Report(217);
                    if(DOS.SetFileSize(handle.Value,100,-1)!=100) return Fail(control,1111);
                    if(DOS.Close(handle.Value)==0) return Fail(control,1112);
                }
                else
                {
                    for(int i=0;i<180;i++) APTR.WriteUInt8(data,i,97);
                    APTR.WriteUInt8(data,180,0);
                    Report(217);
                    if(operation==1 && DOS.SetOwner(CString.FromLiteral("CPX0:target"),0x12345678)==0) return Fail(control,1113);
                    if(operation==2 && DOS.MakeLink("CPX0:soft",(int)data.Raw,1)==0) return Fail(control,1114);
                    if(operation==3 && DOS.DeleteFile("CPX0:target")==0) return Fail(control,1115);
                }
                Report(218);
                if(!FlushJournal()) return Fail(control,1116);
            }
            Report(210); return 0;
        }
        if((flags&2048)!=0)
        {
            if((flags&512)==0)
            {
                Report(217);
                if(DOS.CreateDir("CPX0:new").HasValue||(uint)DOS.IoErr()!=221) return Fail(control,1108);
                Report(219);
            }
            Report(210); return 0;
        }
        if((flags&512)==0)
        {
            var file=DOS.Open("CPX0:target",DOS.FileMode.ReadWrite);
            if(!file.HasValue||DOS.Seek(file.Value,0,1)!=0) return Fail(control,1101);
            for(int n=0;n<512;n++) APTR.WriteUInt8(data,n,(byte)(7*n+3));
            Report(217); // Start observing before sending ACTION_WRITE.
            if(DOS.Write(file.Value,data,512)!=512) return Fail(control,1102);
            Report(218); // The handler has replied to the application.
            if(DOS.Close(file.Value)==0) return Fail(control,1103);
            if(!FlushJournal()) return Fail(control,1109);
        }
        else
        {
            var file=DOS.Open("CPX0:target",DOS.FileMode.OldFile);
            if(!file.HasValue) return Fail(control,1104);
            int size=(flags&1024)==0?512:1024;
            if(DOS.Read(file.Value,data,1024)!=size) return Fail(control,1105);
            for(int n=0;n<size;n++)
                if(APTR.ReadUInt8(data,n)!=(n<512?(byte)(5*n+11):(byte)(7*n+3))) return Fail(control,1106);
            if(DOS.Close(file.Value)==0) return Fail(control,1107);
        }
        Report(210); return 0;
    }
    private static bool FlushJournal()
    {
        APTR port=DOS.DeviceProc("CPX0:");
        if(port.IsNull||DOS.DoPkt(port,(int)DosPacketAction.Flush,0,0,0,0,0)==0) return false;
        Report(216); // This reply, unlike ACTION_WRITE, promises persistence.
        return true;
    }
}
