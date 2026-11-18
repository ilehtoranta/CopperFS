// Derive only device setup and error reporting from our existing guest probe.
// The mixed and stream guest HUNKs remain unchanged.
import fs from 'node:fs';
const source=fs.readFileSync('tools/CopperCPX.Bench/MixedProbe.cs','utf8');
const setup=source.slice(0,source.indexOf('        Report(220);')).replace('public static class Probe','public static class CachePressureProbe');
const tail=source.slice(source.indexOf('    private static int Fail('));
const body=`
        APTR path=APTR.FromPointer(data.Raw+512);
        APTR.WriteUInt8(path,0,67); APTR.WriteUInt8(path,1,80); APTR.WriteUInt8(path,2,88);
        APTR.WriteUInt8(path,3,48); APTR.WriteUInt8(path,4,58); APTR.WriteUInt8(path,5,100);
        APTR.WriteUInt8(path,7,47); APTR.WriteUInt8(path,8,115); APTR.WriteUInt8(path,10,47);
        APTR.WriteUInt8(path,11,102); APTR.WriteUInt8(path,13,0);
        Report(228);
        if(DOS.Inhibit("CPX0:",1)==0||DOS.Inhibit("CPX0:",0)==0) return Fail(30);
        Report(229);
        Report(220);
        for(int round=0;round<3;round++)
            for(int a=0;a<6;a++) for(int b=0;b<6;b++) for(int c=0;c<2;c++)
            {
                if(!ReadFile(a,b,c)) return FailAt(31,(uint)(round*72+a*12+b*2+c));
                for(int hot=0;hot<6;hot++) if(!ReadFile(hot,0,0)) return Fail(32);
            }
        Report(221);
        Report(226); Report(227); Report(222); Report(223); Report(210);
        return 0;
    }
    private static bool ReadFile(int a,int b,int c)
    {
        APTR path=APTR.FromPointer(data.Raw+512);
        APTR.WriteUInt8(path,6,(byte)(48+a)); APTR.WriteUInt8(path,9,(byte)(48+b)); APTR.WriteUInt8(path,12,(byte)(48+c));
        var file=DOS.Open(CString.FromPointer(path.Raw),DOS.FileMode.OldFile);
        if(!file.HasValue) return false;
        bool ok=DOS.Read(file.Value,data,1)==1&&APTR.ReadUInt8(data,0)==(byte)(a*12+b*2+c);
        return DOS.Close(file.Value)!=0&&ok;
    }
`;
fs.writeFileSync('tools/CopperNova.Bench/CachePressureProbe.cs','// Device setup/reporting derived from tools/CopperCPX.Bench/MixedProbe.cs at 6a4380d.\n'+setup+body+tail);
