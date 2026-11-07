using System.Buffers.Binary;
using System.Text;
using Amiga;
#if !COPPERSTART_FILESYSTEM_SEMANTIC_TESTS
using CopperMod.Amiga.Lightweight;
#endif

namespace CopperStart.Floppy.Native.Tests;

/// <summary>Original, standard AmigaDOS media; no commercial disk/ROM assets.</summary>
internal sealed class DosDiskImage(bool ffs, bool highDensity = false, uint firstFileBlock = 22, uint blockCount = 0)
{
    public readonly byte[] Bytes = new byte[checked((int)(blockCount != 0 ? blockCount : highDensity ? 3520u : 1760u) * 512)];
    private uint next = firstFileBlock;
    public uint BlockCount => (uint)Bytes.Length / 512;
    public uint RootBlock => BlockCount / 2;
    public uint BitmapBlock => RootBlock + 1;
    public uint BitmapBlocks => (BlockCount - 2 + 4063) / 4064;
    public uint StartupHeader;
    public uint PayloadHeader;
    public byte[]? ShellSupportBytes;
    private uint commandDirectory;
    private uint scriptDirectory;
    private uint dataDirectory;
    private uint libraryDirectory;
    private uint loaderDirectory;
    public static readonly byte[] Payload = Enumerable.Range(0, 800).Select(i => (byte)(i * 37 + 11)).ToArray();

    public static DosDiskImage Create(bool ffs, bool expansion = false, string? startup = null, string? childScript = null, bool consoleCommands = false, bool omitStartup = false, byte[]? shell = null, byte[]? execute = null, string? consoleScript = null, bool includeLargeFile = true, bool splitShell = true, string volumeName = "NativeBoot", bool highDensity = false, uint firstFileBlock = 22, uint blockCount = 0,
        byte[]? library = null, byte[]? libraryProbe = null, byte[]? guiProbe = null, byte[]? payload = null, uint creationStamp = 0, bool relativeCommand = false, bool hunkMemoryCommand = false)
    {
        var image = new DosDiskImage(ffs, highDensity, firstFileBlock, blockCount);
        if (image.BitmapBlocks > 25) throw new ArgumentOutOfRangeException(nameof(blockCount));
        var hasShell = shell is not null;
        // Keep both DD filesystem profiles and their payloads intact. The full
        // GUI-provider HUNK needs the real bootstrap's separate DF1 path.
        if (splitShell && shell is { Length: > 750000 })
        {
            image.ShellSupportBytes = Create(true, startup: "", shell: shell,
                includeLargeFile: false, splitShell: false, volumeName: "ShellSupport").Bytes;
            shell = null;
        }
        image.Write(0, 0, ffs ? 0x444F5301u : 0x444F5300u);
        image.Write(0, 8, image.RootBlock);
        var boot = new List<ushort>();
        var patches = new List<(int index, string name)>();
        void Lea(string text) { boot.Add(0x43FA); patches.Add((boot.Count, text)); boot.Add(0); }
        if (expansion)
        {
            Lea("expansion.library");
            boot.AddRange([0x7000, 0x4EAE, unchecked((ushort)ExecLvo.OpenLibrary), 0x2040, 0x4A80, 0x670C,
                0x08E8, 0x0006, 0x0022, 0x2248, 0x4EAE, unchecked((ushort)ExecLvo.CloseLibrary)]);
        }
        Lea("dos.library");
        boot.AddRange([0x4EAE, unchecked((ushort)ExecLvo.FindResident), 0x4A80, 0x670A,
            0x2040, 0x2068, 0x0016, 0x7000, 0x4E75, 0x70FF, 0x4E75]);
        var code = new List<byte>();
        foreach (var word in boot) { code.Add((byte)(word >> 8)); code.Add((byte)word); }
        foreach (var (index, name) in patches)
        {
            var displacement = checked((ushort)(code.Count - index * 2));
            code[index * 2] = (byte)(displacement >> 8); code[index * 2 + 1] = (byte)displacement;
            code.AddRange(Encoding.ASCII.GetBytes(name + "\0"));
        }
        code.CopyTo(image.Bytes, 12);
        uint sum = 0;
        for (var i = 0; i < 1024; i += 4) { var n = unchecked(sum + Read(image.Bytes, i)); if (n < sum) n++; sum = n; }
        image.Write(0, 4, ~sum);
        image.Header(image.RootBlock, 0, volumeName, 1);
        image.Write(image.RootBlock, 484, creationStamp);
        image.Write(image.RootBlock, 12, 72);
        image.Write(image.RootBlock, 312, uint.MaxValue);
        for (var page = 0u; page < image.BitmapBlocks; page++) image.Write(image.RootBlock, 316 + (int)page * 4, image.BitmapBlock + page);
        image.Checksum(image.RootBlock);
        var c = image.Directory(image.RootBlock, "C");
        image.commandDirectory = c;
        var s = image.Directory(image.RootBlock, "S");
        image.scriptDirectory = s;
        var data = image.Directory(image.RootBlock, "Data");
        image.dataDirectory = data;
        if (library is not null) image.AddLibrary("probe.library", library);
        if (libraryProbe is not null) image.File(c, "LibraryProbe", libraryProbe);
        if (guiProbe is not null) image.File(c, "WindowProbe", guiProbe);
        image.File(data, "PathProbe", Command());
        image.File(c, "RamProbe", RamCommand());
        image.File(c, "AliasProbe", ConsoleAliasCommand());
        image.File(c, "ExtentProbe", ExtentCommand());
        var command = Command();
        var debugCommand = new byte[command.Length + 12];
        command.AsSpan(0, 24).CopyTo(debugCommand);
        BinaryPrimitives.WriteUInt32BigEndian(debugCommand.AsSpan(20),
            BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(20)) | 0x40000000u);
        BinaryPrimitives.WriteUInt32BigEndian(debugCommand.AsSpan(24), 0x3F1);
        BinaryPrimitives.WriteUInt32BigEndian(debugCommand.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32BigEndian(debugCommand.AsSpan(32), 0x12345678);
        command.AsSpan(24).CopyTo(debugCommand.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(debugCommand.AsSpan(36), 0x400003E9);
        image.File(c, "DebugProbe", debugCommand);
        if (hasShell)
        {
            if (shell is not null)
            { image.loaderDirectory = image.Directory(image.RootBlock, "L"); image.File(image.loaderDirectory, "CopperShell", shell); }
            image.File(c, "SystemProbe", SystemCommand());
        }
        image.File(c, "BootProbe", Command());
        image.File(c, "LockProbe", LockCommand());
        if (execute is not null) image.File(c, "Execute", execute);
        if (consoleScript is not null) image.File(image.RootBlock, "ConsoleScript", Encoding.ASCII.GetBytes(consoleScript));
        if (consoleCommands)
        {
            image.File(c, "ConsoleProbe", ConsoleCommand(false)); image.File(c, "InputProbe", ConsoleCommand(true));
            image.File(c, "WaitProbe", ConsoleCommand(true, wait: 1000000)); image.File(c, "TimeoutProbe", ConsoleCommand(false, wait: 1000));
            image.File(c, "RawProbe", ConsoleCommand(true, raw: true));
        }
        if (relativeCommand) image.File(880, "RelativeProbe", RelativeCommand());
        if (hunkMemoryCommand)
        {
            image.File(880, "HunkProbe", HunkMemoryCommand());
            uint[] h = [0x3F3, 0, 2, 0, 1, 1, 0x40000001, 0x3E9, 1, 0x4E750000, 0x3F2, 0x3EA, 1, 0x12345678, 0x3F2];
            var child = new byte[h.Length * 4];
            for (var i = 0; i < h.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(child.AsSpan(i * 4), h[i]);
            image.File(880, "MemoryHunks", child);
        }
        if (!omitStartup) image.StartupHeader = image.File(s, "Startup-Sequence", Encoding.ASCII.GetBytes(startup ?? (relativeCommand ? "RelativeProbe\n" : "; original DOS boot fixture\nBootProbe hello disk\n")));
        if (childScript is not null) image.File(s, "Child Script", Encoding.ASCII.GetBytes(childScript));
        image.PayloadHeader = image.File(data, "Payload", payload ?? Payload);
        // Exercise extension pointer tables in portable filesystem tests.
        if (includeLargeFile) image.File(data, "Large", Enumerable.Range(0, 42000).Select(i => (byte)(i * 17)).ToArray());
        for (var page = 0u; page < image.BitmapBlocks; page++) Array.Fill(image.Bytes, (byte)0xFF, (int)(image.BitmapBlock + page) * 512 + 4, 508);
        for (uint block = 2; block < 22; block++) image.Allocated(block);
        for (var block = firstFileBlock; block < image.next; block++) image.Allocated(block);
        image.Allocated(image.RootBlock);
        for (var page = 0u; page < image.BitmapBlocks; page++) image.Allocated(image.BitmapBlock + page);
        for (uint block = image.BlockCount; block < image.BitmapBlocks * 4064 + 2; block++) image.Allocated(block);
        image.ChecksumBitmaps();
        return image;
    }

#if !COPPERSTART_FILESYSTEM_SEMANTIC_TESTS
    public void Mount(LightweightA500Machine machine)
    {
        machine.MountAdf(0, Bytes);
        if (ShellSupportBytes is not null)
        { machine.MountAdf(1, ShellSupportBytes); machine.SetDriveWriteProtected(1, true); }
    }

#endif

    // Optional real-command probes copy caller-supplied bytes into this
    // generated fixture. No supplied executable is stored in the repository.
    public void AddCommand(string name, byte[] bytes)
    {
        var first = next;
        File(commandDirectory, name, bytes);
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
    }

    public void AddRootDirectory(string name)
    {
        var first = next;
        Directory(RootBlock, name);
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
    }

    public void AddScript(string name, string text) =>
        AddDrawerFile(scriptDirectory, name, Encoding.ASCII.GetBytes(text));

    public uint AddDrawer(string name, uint parent = 0)
    {
        var first = next;
        var result = Directory(parent == 0 ? RootBlock : parent, name);
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
        return result;
    }

    public void AddDrawerFile(uint drawer, string name, byte[] bytes)
    {
        var first = next;
        File(drawer, name, bytes);
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
    }

    public void AddDataFile(string name, byte[] bytes)
    {
        var first = next;
        File(dataDirectory, name, bytes);
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
    }

    // Consume space with real, linked files, including their extension tables.
    // Unowned allocated bitmap bits would not constitute a valid full volume.
    public void FillLeavingFreeBlocks(int remaining)
    {
        if (!ffs) throw new InvalidOperationException("FFS fixture required.");
        var available = 0;
        for (uint key = 2; key < BlockCount; key++)
        {
            var bit = key - 2;
            if ((Read(Bytes, (int)(BitmapBlock + bit / 4064) * 512 + 4 + (int)(bit % 4064 / 32) * 4) & (1u << (int)(bit % 32))) != 0) available++;
        }
        var budget = available - remaining;
        if (remaining < 0 || budget < 1) throw new ArgumentOutOfRangeException(nameof(remaining));
        var dataBlocks = 0;
        while (1 + dataBlocks + 1 + dataBlocks / 72 <= budget) dataBlocks++;
        AddDataFile("DiskFullFiller", new byte[dataBlocks * 512]);
        var consumed = 1 + dataBlocks + (dataBlocks == 0 ? 0 : (dataBlocks - 1) / 72);
        if (consumed < budget) AddDataFile("DiskFullRemainder", []);
    }

    // Fill with a valid linked file so subsequent guest allocations must use
    // the upper HD half; no orphan bitmap reservations stand in for file data.
    public void FillBeforeBlock(uint firstFreeBlock)
    {
        if (firstFreeBlock >= BlockCount || next >= firstFreeBlock)
            throw new InvalidOperationException("DOS filler must advance within the disk.");
        var dataBlocks = 1;
        while (true)
        {
            var end = next + 1u + (uint)dataBlocks + (uint)((dataBlocks - 1) / 72);
            if (next <= RootBlock && end > RootBlock) end += BitmapBlocks + 1;
            if (end >= firstFreeBlock) break;
            dataBlocks++;
        }
        AddDataFile("LowerHalf", new byte[dataBlocks * (ffs ? 512 : 488)]);
    }

    public void AddLibrary(string name, byte[] bytes)
    {
        var first = next;
        if (libraryDirectory == 0) libraryDirectory = Directory(RootBlock, "Libs");
        File(libraryDirectory, name, bytes);
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
    }

    public void AddDesktop(byte[] bytes)
        => AddLoader("CopperDesktop", bytes);

    public void AddLoader(string name, byte[] bytes)
    {
        var first = next;
        if (loaderDirectory == 0) loaderDirectory = Directory(RootBlock, "L");
        File(loaderDirectory, name, bytes);
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
    }

    public void AddDataTypes(string sourceDirectory)
    {
        var first = next;
        var devices = Directory(RootBlock, "Devs");
        var types = Directory(devices, "DataTypes");
        foreach (var path in System.IO.Directory.GetFiles(sourceDirectory))
            if (!Path.GetFileName(path).StartsWith('.')) File(types, Path.GetFileName(path), System.IO.File.ReadAllBytes(path));
        for (var block = first; block < next; block++) Allocated(block);
        ChecksumBitmaps();
    }

    private void Allocated(uint block)
    {
        var bit = block - 2;
        var page = BitmapBlock + bit / 4064;
        var offset = 4 + (int)(bit % 4064 / 32) * 4;
        Write(page, offset, Read(Bytes, (int)page * 512 + offset) & ~(1u << (int)(bit % 32)));
    }

    private void ChecksumBitmaps()
    {
        for (var page = 0u; page < BitmapBlocks; page++) Checksum(BitmapBlock + page, 0);
    }

    private void Header(uint block, uint parent, string name, int type)
    {
        Write(block, 0, 2); Write(block, 4, block == RootBlock ? 0 : block);
        Write(block, 500, parent); Write(block, 508, unchecked((uint)type));
        Bytes[block * 512 + 432] = checked((byte)name.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(Bytes, (int)block * 512 + 433);
    }

    private void Link(uint parent, uint block, string name)
    {
        uint hash = (uint)name.Length;
        foreach (var c in name) hash = (hash * 13 + char.ToUpperInvariant(c)) & 0x7FF;
        var slot = 24 + (int)(hash % 72) * 4;
        Write(block, 496, Read(Bytes, (int)parent * 512 + slot));
        Write(parent, slot, block);
        Checksum(block); Checksum(parent);
    }

    private uint Directory(uint parent, string name)
    { var block = AllocateBlock(); Header(block, parent, name, 2); Link(parent, block, name); return block; }

    private uint AllocateBlock()
    {
        if (next == RootBlock) next = BitmapBlock + BitmapBlocks;
        if (next >= BlockCount) throw new InvalidOperationException("Generated DOS fixture exceeds its disk geometry.");
        return next++;
    }

    private uint File(uint parent, string name, byte[] bytes)
    {
        var header = AllocateBlock();
        Header(header, parent, name, -3); Write(header, 324, (uint)bytes.Length);
        var table = header;
        var payload = ffs ? 512 : 488;
        var count = (bytes.Length + payload - 1) / payload;
        for (var index = 0; index < count; index++)
        {
            if (index != 0 && index % 72 == 0)
            {
                var extension = AllocateBlock();
                Write(table, 504, extension); Checksum(table);
                table = extension;
                Write(table, 0, 16); Write(table, 4, table); Write(table, 500, header); Write(table, 508, unchecked((uint)-3));
            }
            var data = AllocateBlock();
            Write(table, 8, (uint)(index % 72 + 1)); Write(table, 308 - index % 72 * 4, data);
            if (index == 0) Write(header, 16, data);
            var length = Math.Min(payload, bytes.Length - index * payload);
            if (!ffs)
            {
                Write(data, 0, 8); Write(data, 4, header); Write(data, 8, (uint)(index + 1)); Write(data, 12, (uint)length);
                // The next block may be separated by an extension table.
                var following = next == RootBlock ? BitmapBlock + BitmapBlocks : next;
                if ((index + 1) % 72 == 0) { following++; if (following == RootBlock) following = BitmapBlock + BitmapBlocks; }
                Write(data, 16, index + 1 == count ? 0 : following);
            }
            bytes.AsSpan(index * payload, length).CopyTo(Bytes.AsSpan((int)data * 512 + (ffs ? 0 : 24)));
            if (!ffs) Checksum(data);
        }
        Checksum(table); Link(parent, header, name);
        return header;
    }

    public void Write(uint block, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)block * 512 + offset), value);
    public void Checksum(uint block, int offset = 20)
    {
        Write(block, offset, 0); uint sum = 0;
        for (var i = 0; i < 512; i += 4) sum = unchecked(sum + Read(Bytes, (int)block * 512 + i));
        Write(block, offset, unchecked(0u - sum));
    }
    private static uint Read(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));

    private static byte[] HunkMemoryCommand()
    {
        var words = new List<ushort>();
        var patches = new List<(int index, string text)>();
        void Lea(string text) { words.Add(0x43FA); patches.Add((words.Count, text)); words.Add(0); }
        words.AddRange([0x2C78, 4]); Lea("dos.library");
        words.AddRange([0x7000, 0x4EAE, unchecked((ushort)ExecLvo.OpenLibrary), 0x2C40]);
        Lea("DF0:MemoryHunks");
        words.AddRange([0x2209, 0x4EAE, unchecked((ushort)DosLvo.LoadSeg), 0x21C0, 0x0950,
            0xE588, 0x2040, 0x2010, 0xE588, 0x5880, 0x21C0, 0x0954,
            0x2240, 0x21D1, 0x095C, 0x2C78, 4, 0x4EAE, unchecked((ushort)ExecLvo.TypeOfMem),
            0x21C0, 0x0958, 0x21FC, 0x484E, 0x4B31, 0x0940, 0x60FE]);
        var code = new List<byte>(); foreach (var w in words) { code.Add((byte)(w >> 8)); code.Add((byte)w); }
        foreach (var (index, text) in patches)
        {
            var d = checked((ushort)(code.Count - index * 2)); code[index * 2] = (byte)(d >> 8); code[index * 2 + 1] = (byte)d;
            code.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
        }
        while (code.Count % 4 != 0) code.Add(0);
        var hunk = new byte[32 + code.Count + 4];
        uint[] header = [0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }

    private static byte[] RelativeCommand()
    {
        var words = new List<ushort>();
        var patches = new List<(int index, string text)>();
        void Lea(ushort opcode, string text) { words.Add(opcode); patches.Add((words.Count, text)); words.Add(0); }
        // A real CLI command, loaded from the root by its relative name. Keep
        // pr_FileSystemTask empty so repeated reads must use pr_CurrentDir.
        words.AddRange([0x2C78, 4]);
        Lea(0x43FA, "dos.library");
        words.AddRange([0x7000, 0x4EAE, unchecked((ushort)ExecLvo.OpenLibrary), 0x2C40]);
        Lea(0x41FA, "DF0:Data");
        words.AddRange([0x2208, 0x74FE, 0x4EAE, unchecked((ushort)DosLvo.Lock),
            0x2200, 0x4EAE, unchecked((ushort)DosLvo.CurrentDir), 0x264E,
            0x2C78, 4, 0x93C9, 0x4EAE, unchecked((ushort)ExecLvo.FindTask), 0x2040,
            0x42A8, (ushort)DosLayout.Process.FileSystemTask,
            0x21E8, (ushort)DosLayout.Process.CurrentDirectory, 0x0980, 0x2C4B]);
        for (var i = 0; i < 3; i++)
        {
            Lea(0x41FA, i == 2 ? ":Data/Payload" : "Payload");
            words.AddRange([0x2208, 0x243C, 0, 1005, 0x4EAE, unchecked((ushort)DosLvo.Open),
                0x21C0, (ushort)(0x950 + i * 4), 0x2200, 0x243C, 0, (ushort)(0xA00 + i * 4),
                0x7604, 0x4EAE, unchecked((ushort)DosLvo.Read), 0x21C0, (ushort)(0x960 + i * 4),
                0x2238, (ushort)(0x950 + i * 4), 0x4EAE, unchecked((ushort)DosLvo.Close)]);
        }
        words.AddRange([0x2C78, 4, 0x93C9, 0x4EAE, unchecked((ushort)ExecLvo.FindTask), 0x2040,
            0x21E8, (ushort)DosLayout.Process.CurrentDirectory, 0x0984,
            0x21FC, 0x5245, 0x4C31, 0x0940, 0x7000, 0x4E75]);
        var code = new List<byte>(); foreach (var w in words) { code.Add((byte)(w >> 8)); code.Add((byte)w); }
        foreach (var (index, text) in patches)
        {
            var d = checked((ushort)(code.Count - index * 2)); code[index * 2] = (byte)(d >> 8); code[index * 2 + 1] = (byte)d;
            code.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
        }
        while (code.Count % 4 != 0) code.Add(0);
        var hunk = new byte[32 + code.Count + 4];
        uint[] header = [0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }

    private static byte[] Command()
    {
        var words = new List<ushort>();
        var patches = new List<(int index, ushort opcode, string name)>();
        void Lea(ushort opcode, string name) { words.Add(opcode); patches.Add((words.Count, opcode, name)); words.Add(0); }
        words.AddRange([0x43FA, 0, 0x21C9, 0x099C]); // record an address in this command's code hunk
        // CLI stack bounds: SP + return-address/size pair - 4(SP).
        words.AddRange([0x21EF, 0x0004, 0x0990, 0x224F, 0x5089, 0x93EF, 0x0004, 0x21C9, 0x0994,
            0x2278, 0x0004, 0x2269, 0x0114,
            0x2229, 0x00AC, 0xE589, 0x2241, 0x21E9, 0x0010, 0x09A0, 0x21E9, 0x003C, 0x09A4,
            // Snapshot the BSTR while the command is active; the shell clears it on return.
            0x2238, 0x09A0, 0xE589, 0x2241, 0x45F8, 0x0B20, 0x7209, 0x14D9, 0x51C9, 0xFFFC,
            0x2278, 0x0004, 0x2269, 0x0114, 0x21E9, 0x003A, 0x0998, 0x21E9, 0x003E, 0x09A8,
            0x21C0, 0x0958, 0x21C8, 0x095C, 0x2448, 0x47F8, 0x0970, 0x720A, 0x16DA, 0x51C9, 0xFFFC, 0x2C78, 0x0004]);
        Lea(0x43FA, "dos.library");
        words.AddRange([0x7000, 0x4EAE, unchecked((ushort)ExecLvo.OpenLibrary), 0x2C40]);
        Lea(0x41FA, "sys:data/payload");
        words.AddRange([0x2208, 0x243C, 0, 1005, 0x4EAE, unchecked((ushort)DosLvo.Open), 0x21C0, 0x0950,
            0x2200, 0x243C, 0, 480, 0x76FF, 0x4EAE, unchecked((ushort)DosLvo.Seek), 0x21C0, 0x0960,
            0x2238, 0x0950, 0x243C, 0, 0x0A00, 0x263C, 0, 80, 0x4EAE, unchecked((ushort)DosLvo.Read), 0x21C0, 0x0954,
            0x4EAE, unchecked((ushort)DosLvo.IoErr), 0x21C0, 0x096C,
            0x2238, 0x0950, 0x4EAE, unchecked((ushort)DosLvo.Close)]);
        Lea(0x41FA, "DF0:Data/Payload");
        words.AddRange([0x2208, 0x243C, 0, 1006, 0x4EAE, unchecked((ushort)DosLvo.Open), 0x21C0, 0x0964,
            0x4EAE, unchecked((ushort)DosLvo.IoErr), 0x21C0, 0x0968,
            0x223C, 0x1234, 0x5678, 0x4EAE, 0xFFEE, 0x21C0, 0x0988, 0x21C1, 0x0984,
            0x4EAE, 0xFFF4, 0x4EAE, 0xFFF4, 0x7000, 0x302E, 0x0020, 0x21C0, 0x098C,
            0x21FC, 0x444F, 0x5342, 0x0940, 0x7000, 0x4E75]);
        var code = new List<byte>();
        foreach (var word in words) { code.Add((byte)(word >> 8)); code.Add((byte)word); }
        foreach (var (index, _, name) in patches)
        {
            var displacement = checked((ushort)(code.Count - index * 2));
            code[index * 2] = (byte)(displacement >> 8); code[index * 2 + 1] = (byte)displacement;
            code.AddRange(Encoding.ASCII.GetBytes(name + "\0"));
        }
        while (code.Count % 4 != 0) code.Add(0);
        var hunk = new byte[32 + code.Count + 4];
        var header = new uint[] { 0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4 };
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }

    internal static byte[] LockCommand(string path = "SYS:Data/Payload")
    {
        // Convert a caller-owned lock through public DOS, then read and close
        // the returned handle. No private filesystem records are fabricated.
        var words = new List<ushort> { 0x2C78, 4, 0x43FA, 0 };
        words.AddRange([0x7000, 0x4EAE, unchecked((ushort)ExecLvo.OpenLibrary), 0x2C40,
            0x41FA, 0]);
        var pathPatch = words.Count - 1;
        words.AddRange([0x2208, 0x74FE, 0x4EAE, unchecked((ushort)DosLvo.Lock), 0x21C0, 0x09A0,
            0x2200, 0x4EAE, unchecked((ushort)DosLvo.OpenFromLock), 0x21C0, 0x09A4,
            0x2200, 0x243C, 0, 0x0B80, 0x7640, 0x4EAE, unchecked((ushort)DosLvo.Read), 0x21C0, 0x09A8,
            0x4EAE, unchecked((ushort)DosLvo.IoErr), 0x21C0, 0x09AC,
            0x2238, 0x09A4, 0x4EAE, unchecked((ushort)DosLvo.Close), 0x21C0, 0x09B0,
            0x224E, 0x2C78, 4, 0x4EAE, unchecked((ushort)ExecLvo.CloseLibrary),
            0x21FC, 0x4C4F, 0x434B, 0x09B4, 0x7000, 0x4E75]);
        var code = new List<byte>();
        foreach (var word in words) { code.Add((byte)(word >> 8)); code.Add((byte)word); }
        void Text(int patch, string text)
        {
            var displacement = checked((ushort)(code.Count - patch * 2));
            code[patch * 2] = (byte)(displacement >> 8); code[patch * 2 + 1] = (byte)displacement;
            code.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
        }
        Text(3, "dos.library"); Text(pathPatch, path);
        while (code.Count % 4 != 0) code.Add(0);
        var hunk = new byte[36 + code.Count];
        uint[] header = [0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }

    private static byte[] SystemCommand()
    {
        // A disk command uses the public DOS vectors, exercising the resident
        // Shell's child entry and completion rather than the boot driver.
        var words = new List<ushort> { 0x2C78, 4, 0x43FA, 0 };
        words.AddRange([0x7000, 0x4EAE, unchecked((ushort)ExecLvo.OpenLibrary), 0x2C40,
            0x41FA, 0]);
        var commandPatch = words.Count - 1;
        words.AddRange([0x2208, 0x7400, 0x4EAE, unchecked((ushort)DosLvo.SystemTagList), 0x21C0, 0x09E0,
            0x7001, 0x21C0, 0x09F4,
            0x41FA, 0]);
        var executePatch = words.Count - 1;
        words.AddRange([0x2208, 0x7400, 0x7600, 0x4EAE, unchecked((ushort)DosLvo.Execute), 0x21C0, 0x09E4,
            0x7002, 0x21C0, 0x09F4,
            0x41FA, 0]);
        var quitPatch = words.Count - 1;
        words.AddRange([0x2208, 0x7400, 0x4EAE, unchecked((ushort)DosLvo.SystemTagList), 0x21C0, 0x09EC,
            0x4EAE, unchecked((ushort)DosLvo.IoErr), 0x21C0, 0x09F0,
            0x224E, 0x2C78, 4, 0x4EAE, unchecked((ushort)ExecLvo.CloseLibrary),
            0x21FC, 0x5359, 0x5354, 0x09E8, 0x7000, 0x4E75]);
        var code = new List<byte>();
        foreach (var word in words) { code.Add((byte)(word >> 8)); code.Add((byte)word); }
        void Text(int patch, string text)
        {
            var displacement = checked((ushort)(code.Count - patch * 2));
            code[patch * 2] = (byte)(displacement >> 8); code[patch * 2 + 1] = (byte)displacement;
            code.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
        }
        Text(3, "dos.library");
        Text(commandPatch, "BootProbe hello disk\n");
        Text(executePatch, "BootProbe hello disk\n");
        Text(quitPatch, "Quit 5\n");
        while (code.Count % 4 != 0) code.Add(0);
        var hunk = new byte[36 + code.Count];
        uint[] header = [0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }

    private static byte[] ConsoleCommand(bool read, uint wait = 0, bool raw = false)
    {
        var words = new List<ushort>(); var patches = new List<(int index, string text)>();
        void Lea(ushort opcode, string text) { words.Add(opcode); patches.Add((words.Count, text)); words.Add(0); }
        words.AddRange([0x2C78, 0x0004]); Lea(0x43FA, "dos.library");
        words.AddRange([0x7000, 0x4EAE, unchecked((ushort)ExecLvo.OpenLibrary), 0x2C40,
            0x4EAE, unchecked((ushort)DosLvo.Output), 0x2200, 0x21C0, 0x09C4,
            0x4EAE, unchecked((ushort)DosLvo.IsInteractive), 0x21C0, 0x09C8]);
        Lea(0x41FA, "DOS console ready\n");
        words.AddRange([0x2408, 0x2238, 0x09C4, 0x7612, 0x4EAE, unchecked((ushort)DosLvo.Write), 0x21C0, 0x09CC]);
        if (wait != 0 || raw) words.AddRange([0x4EAE, unchecked((ushort)DosLvo.Input), 0x21C0, 0x09D8]);
        if (wait != 0) words.AddRange([0x2200, 0x243C, (ushort)(wait >> 16), (ushort)wait,
            0x4EAE, unchecked((ushort)DosLvo.WaitForChar), 0x21C0, 0x09D4]);
        if (raw) words.AddRange([0x2200, 0x7401, 0x4EAE, unchecked((ushort)DosLvo.SetMode), 0x21C0, 0x09DC]);
        if (read) words.AddRange([0x4EAE, unchecked((ushort)DosLvo.Input), 0x2200, 0x243C, 0, 0x0B00,
            0x7640, 0x4EAE, unchecked((ushort)DosLvo.Read), 0x21C0, 0x09D0]);
        if (raw) words.AddRange([0x2238, 0x09D8, 0x7400, 0x4EAE, unchecked((ushort)DosLvo.SetMode)]);
        words.AddRange([0x224E, 0x2C78, 0x0004, 0x4EAE, unchecked((ushort)ExecLvo.CloseLibrary),
            0x21FC, 0x434F, 0x4E53, 0x09C0, 0x7000, 0x4E75]);
        var code = new List<byte>(); foreach (var w in words) { code.Add((byte)(w >> 8)); code.Add((byte)w); }
        foreach (var (index, text) in patches)
        {
            var d = checked((ushort)(code.Count - index * 2)); code[index * 2] = (byte)(d >> 8); code[index * 2 + 1] = (byte)d;
            code.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
        }
        while (code.Count % 4 != 0) code.Add(0);
        var hunk = new byte[32 + code.Count + 4];
        uint[] header = [0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }

    private static byte[] ExtentCommand()
    {
        var command = Command(); var code = new List<byte>();
        ushort[] prefix = [0x43FA, 0, 0x2229, 0xFFFA, 0xE589, 0x2241, 0x21E9, 4, 0x09B0,
            0x2211, 0xE589, 0x2241, 0x21E9, 4, 0x09B4];
        foreach (var word in prefix) { code.Add((byte)(word >> 8)); code.Add((byte)word); }
        code.AddRange(command.AsSpan(32, command.Length - 36).ToArray());
        while ((code.Count & 3) != 0) code.Add(0);
        var image = new List<byte>();
        void Word(uint value) { image.Add((byte)(value >> 24)); image.Add((byte)(value >> 16)); image.Add((byte)(value >> 8)); image.Add((byte)value); }
        foreach (var word in new uint[] { 0x3F3, 0, 3, 0, 2, (uint)code.Count / 4, 5000, 5000, 0x3E9, (uint)code.Count / 4 }) Word(word);
        image.AddRange(code); Word(0x3F2); Word(0x3F1); Word(5000);
        for (var i = 0; i < 5000; i++) Word(0x12345678);
        Word(0x3EA); Word(5000); for (var i = 0; i < 5000; i++) Word(0xBEEFCAFE);
        Word(0x3F2); Word(0x3EB); Word(5000); Word(0x3F2);
        return image.ToArray();
    }

    private static byte[] ConsoleAliasCommand()
    {
        var words = new List<ushort>(); var patches = new List<(int index, string text)>();
        void Lea(string text) { words.Add(0x41FA); patches.Add((words.Count, text)); words.Add(0); }
        void Call(int lvo) => words.AddRange([0x4EAE, unchecked((ushort)lvo)]);
        words.AddRange([0x2C78, 4, 0x43FA, 0]); patches.Add((3, "dos.library"));
        words.Add(0x7000); Call(ExecLvo.OpenLibrary); words.Add(0x2C40);
        Call(DosLvo.GetConsoleTask); words.AddRange([0x21C0, 0x09BC]);
        // The boot process can have no console task even when its input is a
        // console. Select the input's public handler, as the shared Shell does.
        Call(DosLvo.Input);
        words.AddRange([0xE588, 0x2040, 0x2028, 8, 0x21C0, 0x09D8, 0x2200]);
        Call(DosLvo.SetConsoleTask);
        words.AddRange([0x2238, 0x09D8, 0x7419, 0x263C, 0, 0x02E0, 0x7800, 0x7A00, 0x7C00, 0x7E00]);
        Call(DosLvo.DoPkt); words.AddRange([0x21C0, 0x09DC]); // ACTION_DISK_INFO: BPTR $B80
        // A current directory must not redirect the special "*" console name
        // to a filesystem file. Keep two aliases open at once as well.
        Lea("SYS:Data"); words.AddRange([0x2208, 0x74FE]); Call(DosLvo.Lock);
        words.AddRange([0x21C0, 0x09C0, 0x2200]); Call(DosLvo.CurrentDir);
        words.AddRange([0x21C0, 0x09C4]);
        Lea("*"); words.AddRange([0x2208, 0x243C, 0, 1005]); Call(DosLvo.Open);
        words.AddRange([0x21C0, 0x09E0, 0x2200]); Call(DosLvo.IsInteractive); words.AddRange([0x21C0, 0x09E4]);
        words.AddRange([0x2038, 0x09E0, 0xE588, 0x2040, 0x2028, 8, 0x21C0, 0x09D0]);
        Lea("*"); words.AddRange([0x2208, 0x243C, 0, 1005]); Call(DosLvo.Open);
        words.AddRange([0x21C0, 0x09C8, 0x2200]); Call(DosLvo.IsInteractive); words.AddRange([0x21C0, 0x09CC]);
        words.AddRange([0x2038, 0x09C8, 0xE588, 0x2040, 0x2028, 8, 0x21C0, 0x09D4]);
          // Only the complete one-byte name is special. A longer name that
          // starts with '*' must still resolve against the current directory.
          Lea("*ordinary"); words.AddRange([0x2208, 0x243C, 0, 1005]); Call(DosLvo.Open);
          words.AddRange([0x21C0, 0x09B0, 0x2200]); Call(DosLvo.IsInteractive);
          words.AddRange([0x21C0, 0x09B4, 0x2238, 0x09B0]); Call(DosLvo.Close);
        words.AddRange([0x2238, 0x09C8]); Call(DosLvo.Close);
        words.AddRange([0x2238, 0x09E0]); Call(DosLvo.Close);
        words.AddRange([0x2238, 0x09C4]); Call(DosLvo.CurrentDir);
        words.Add(0x2200); Call(DosLvo.UnLock);
        Lea("RAW:0/0/640/200/Alias probe"); words.AddRange([0x2208, 0x243C, 0, 1005]); Call(DosLvo.Open);
        words.AddRange([0x21C0, 0x09E8, 0x2200]); Call(DosLvo.IsInteractive); words.AddRange([0x21C0, 0x09EC]);
        words.AddRange([0x21FC, 0x5241, 0x5730, 0x09F8]); // RAW0: aliases and interactivity results are complete.
        words.AddRange([0x2238, 0x09E8, 0x243C, 0, 0x0B00, 0x7601]); Call(DosLvo.Read); words.AddRange([0x21C0, 0x09F0]);
        words.AddRange([0x2238, 0x09E8]); Call(DosLvo.Close);
        words.AddRange([0x21FC, 0x5241, 0x5731, 0x09F4, 0x2238, 0x09BC]);
        Call(DosLvo.SetConsoleTask); words.AddRange([0x7000, 0x4E75]);
        var code = new List<byte>(); foreach (var w in words) { code.Add((byte)(w >> 8)); code.Add((byte)w); }
        foreach (var (index, text) in patches)
        { var displacement = checked((ushort)(code.Count - index * 2)); code[index * 2] = (byte)(displacement >> 8); code[index * 2 + 1] = (byte)displacement; code.AddRange(Encoding.ASCII.GetBytes(text + "\0")); }
        while ((code.Count & 3) != 0) code.Add(0);
        var hunk = new byte[36 + code.Count];
        uint[] header = [0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }

    private static byte[] RamCommand()
    {
        var words = new List<ushort>(); var patches = new List<(int index, string text)>();
        void Lea(ushort opcode, string text) { words.Add(opcode); patches.Add((words.Count, text)); words.Add(0); }
        void Call(int lvo) => words.AddRange([0x4EAE, unchecked((ushort)lvo)]);
        words.AddRange([0x2C78, 4]); Lea(0x43FA, "dos.library");
        words.Add(0x7000); Call(ExecLvo.OpenLibrary); words.Add(0x2C40);
        Lea(0x41FA, "RAM:Test"); words.AddRange([0x2208, 0x243C, 0, 1006]); Call(DosLvo.Open);
        words.AddRange([0x2E00, 0x21C0, 0x0A00, 0x2207]); Lea(0x41FA, "abc");
        words.AddRange([0x2408, 0x7603]); Call(DosLvo.Write); words.AddRange([0x21C0, 0x0A04]);
        words.AddRange([0x2207, 0x7400, 0x76FF]); Call(DosLvo.Seek); words.AddRange([0x21C0, 0x0A08]);
        words.AddRange([0x2207, 0x243C, 0, 0x0A20, 0x7603]); Call(DosLvo.Read); words.AddRange([0x21C0, 0x0A0C]);
        words.Add(0x2207); Call(DosLvo.Close); words.AddRange([0x21C0, 0x0A10]);
        Lea(0x41FA, "RAM:Test"); words.AddRange([0x2208, 0x74FE]); Call(DosLvo.Lock);
        words.AddRange([0x2E00, 0x21C0, 0x0A14, 0x2207, 0x243C, 0, 0x0B00]); Call(DosLvo.Examine);
        words.AddRange([0x21C0, 0x0A18, 0x2207]); Call(DosLvo.UnLock);
        Lea(0x41FA, "RAM:Dir"); words.Add(0x2208); Call(DosLvo.CreateDir);
        words.AddRange([0x21C0, 0x0A24, 0x2200]); Call(DosLvo.UnLock);
        Lea(0x41FA, "RAM:Dir"); words.Add(0x2208); Call(DosLvo.CreateDir);
        words.AddRange([0x21C0, 0x0A28]); Call(DosLvo.IoErr); words.AddRange([0x21C0, 0x0A2C]);
        words.AddRange([0x21FC, 0x5241, 0x4D31, 0x0A1C, 0x7000, 0x4E75]);
        var code = new List<byte>(); foreach (var w in words) { code.Add((byte)(w >> 8)); code.Add((byte)w); }
        foreach (var (index, text) in patches)
        {
            var d = checked((ushort)(code.Count - index * 2)); code[index * 2] = (byte)(d >> 8); code[index * 2 + 1] = (byte)d;
            code.AddRange(Encoding.ASCII.GetBytes(text + "\0"));
        }
        while (code.Count % 4 != 0) code.Add(0);
        var hunk = new byte[36 + code.Count];
        uint[] header = [0x3F3, 0, 1, 0, 0, (uint)code.Count / 4, 0x3E9, (uint)code.Count / 4];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(i * 4), header[i]);
        code.CopyTo(hunk, 32); BinaryPrimitives.WriteUInt32BigEndian(hunk.AsSpan(32 + code.Count), 0x3F2);
        return hunk;
    }
}
