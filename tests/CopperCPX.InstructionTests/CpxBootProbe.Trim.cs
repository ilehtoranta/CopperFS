using Amiga;
namespace CopperCPX.InstructionTests;

public static partial class CpxBootProbe
{
    private static bool PrivateTrim(APTR control,APTR port,APTR record,bool supported)
    {
        APTR.WriteUInt32(control,36,950); APTR.WriteUInt32(record,0,32); APTR.WriteUInt32(record,4,1);
        uint proposed=0,ranges=0;
        for(int mode=2;mode>=0;mode--)
        {
            APTR.WriteUInt32(record,8,(uint)mode);
            int result=DOS.DoPkt(port,0x43535452,(int)record.Raw,0,0,0,0);
            uint blocks=APTR.ReadUInt32(record,12),count=APTR.ReadUInt32(record,16);
            if(!supported)
            {
                if(result!=0||(uint)DOS.IoErr()!=209||blocks!=0||count!=0||APTR.ReadUInt32(record,20)!=0) return false;
                continue;
            }
            if(result!=-1||APTR.ReadUInt32(record,20)!=4096||APTR.ReadUInt32(record,24)!=1||APTR.ReadUInt32(record,28)!=0) return false;
            if(mode==2&&(blocks!=0||count!=0)) return false;
            if(mode==1) { proposed=blocks; ranges=count; if(proposed==0||ranges==0) return false; }
            if(mode==0&&(proposed!=blocks||ranges!=count)) return false;
        }
        APTR.WriteUInt32(control,1800,ranges); APTR.WriteUInt32(control,36,0); return true;
    }
}
