using Amiga;
using CopperNova.Prototype;
using CopperSharp.Compiler;
using CopperCPX.Amiga;

namespace CopperNova.Amiga;

public static class NativeNovaFormat
{
    [M68kEntryPoint]
    public static int Entry(int length, CONST_STRPTR commandLine)
    {
        APTR dos = Exec.OpenLibraryRaw("dos.library", 37);
        if (dos.IsNull) return 20;

        DOS.DOSLibraryBase = dos;
        APTR args = DOS.AllocDosObject((uint)DosObjectType.RdArgs, APTR.Null);
        APTR values = APTR.FromPointer(Exec.AllocMem(48, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        int result = 20;
        if (args.IsNotNull && values.IsNotNull)
        {
            APTR.WriteUInt32(args, DosLayout.CSource.Buffer, commandLine.Raw);
            APTR.WriteUInt32(args, DosLayout.CSource.Length, (uint)length);
            APTR.WriteUInt32(args, DosLayout.RDArgs.Flags, (uint)DosRdArgsFlags.NoPrompt);
            APTR parsed = DOS.ReadArgs("DEVICE/A,INITIALIZE/S,V7/S,NAME=LABEL/K", values, args);
            if (parsed.IsNull || APTR.ReadUInt32(values, 4) == 0)
                DOS.PutStr("Usage: CopperNovaFormat <partition:> INITIALIZE [NAME <volume name>]\n");
            else
                result = Initialize(APTR.FromPointer(APTR.ReadUInt32(values, 0)),values);
            if (parsed.IsNotNull) DOS.FreeArgs(parsed);
        }
        if (values.IsNotNull) Exec.FreeMem(values.Raw, 48);
        if (args.IsNotNull) DOS.FreeDosObject((uint)DosObjectType.RdArgs, args);
        Exec.CloseLibrary(dos);
        return result;
    }

    private static int Initialize(APTR device,APTR values)
    {
        int colon = 0;
        while (colon < 31 && APTR.ReadUInt8(device, colon) > 32 && APTR.ReadUInt8(device, colon) != ':' &&
               APTR.ReadUInt8(device, colon) != '/')
            colon++;
        if (colon == 0 || APTR.ReadUInt8(device, colon) != ':' || APTR.ReadUInt8(device, colon + 1) != 0 ||
            !IsDevice(device, colon))
        {
            DOS.PutStr("Specify a partition device with RDB type CNP2 (0x434E5032).\n");
            return 20;
        }

        uint label=APTR.ReadUInt32(values,12);
        APTR bstr=APTR.FromPointer(values.Raw+16);
        if(label!=0)
        {
            APTR text=APTR.FromPointer(label);
            int length=0;
            while(length<=30 && APTR.ReadUInt8(text,length)!=0) length++;
            if(length==0 || length>30) { DOS.PutStr("Volume names must contain 1 to 30 bytes.\n"); return 20; }
            APTR.WriteUInt8(bstr,0,(byte)length);
            for(int i=0;i<length;i++) APTR.WriteUInt8(bstr,i+1,APTR.ReadUInt8(text,i));
        }

        // Suppress Insert Volume requesters when the target cannot start.
        APTR process = Exec.FindTask(CString.FromPointer(0));
        uint window = APTR.ReadUInt32(process, 184);
        APTR.WriteUInt32(process, 184, uint.MaxValue);
        APTR port = DOS.DeviceProc(CString.FromPointer(device.Raw));
        const int action = (int)NovaEngine<NativeCpxMemory>.InitializeCnp2;
        const int cnp2 = (int)NovaEngine<NativeCpxMemory>.PackedMagic;
        bool ready = port.IsNotNull && DOS.DoPkt2(port, action, cnp2, 0) == cnp2;
        bool success = ready && (label==0 ? DOS.DoPkt2(port, action, cnp2, 8) : DOS.DoPkt3(port,action,cnp2,9,(int)bstr.Raw)) != 0;
        DOS.Error error = DOS.IoErr();
        APTR.WriteUInt32(process, 184, window);
        if (success)
        {
            DOS.PutStr("CNP2 partition initialized and mounted.\n");
            return 0;
        }
        if (ready)
            DOS.PrintFault(error, "CopperNovaFormat");
        else
            DOS.PutStr("The device did not report an uninitialized CopperNova CNP2 partition.\n");
        return 20;
    }

    private static bool IsDevice(APTR device, int colon)
    {
        const uint flags = (uint)(DosListLockFlags.Read | DosListLockFlags.Devices);
        APTR list = DOS.LockDosList(flags);
        if (list.IsNull) return false;

        APTR.WriteUInt8(device, colon, 0);
        bool found = DOS.FindDosEntry(list, CString.FromPointer(device.Raw), (uint)DosListLockFlags.Devices).IsNotNull;
        APTR.WriteUInt8(device, colon, (byte)':');
        DOS.UnLockDosList(flags);
        return found;
    }
}
