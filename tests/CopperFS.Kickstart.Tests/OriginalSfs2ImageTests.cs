using Amiga;
using Copper68k;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

// Full and VerifyFull are adapted from NativeSfs2Probe at research commit 4a902a1.
public static partial class OriginalSfs2Probe
{
    public static int MutateOriginal() => Run(1);
    public static int VerifyMutations() => Run(2);

    private static int Full(APTR store)
    {
        var error = ReadBase(store); if (error != 0) return error;
        var data = APTR.FromPointer(store.Raw + 512);
        for (var i = 0; i < 8195; i++) APTR.WriteUInt8(data, i, (byte)(i * 29 + 7));
        var file = DOS.Open("SEQ0:CopperWrite", DOS.FileMode.NewFile); if (!file.HasValue) return 27;
        if (DOS.Write(file.Value, data, 8195) != 8195 || DOS.Close(file.Value) == 0) return 28;
        var dir = DOS.CreateDir("SEQ0:CopperDir"); if (!dir.HasValue) return 29; DOS.UnLock(dir.Value);
        file = DOS.Open("SEQ0:CopperDir/BeforeRename", DOS.FileMode.NewFile); if (!file.HasValue) return 30;
        for (var i = 0; i < 1027; i++) APTR.WriteUInt8(data, i, (byte)(i * 17 + 11));
        if (DOS.Write(file.Value, data, 1027) != 1027 || DOS.Close(file.Value) == 0) return 31;
        if (DOS.Rename("SEQ0:CopperDir/BeforeRename", "SEQ0:CopperDir/Renamed") == 0 ||
            DOS.SetComment("SEQ0:CopperDir/Renamed", "Written by CopperSFS2") == 0 ||
            DOS.SetProtection("SEQ0:CopperDir/Renamed", 128) == 0) return 32;
        APTR.WriteUInt32(store, 256, 1234); APTR.WriteUInt32(store, 260, 5); APTR.WriteUInt32(store, 264, 100);
        if (DOS.SetFileDate("SEQ0:CopperDir/Renamed", APTR.FromPointer(store.Raw + 256)) == 0) return 33;
        for (var i = 0; i < 7; i++) APTR.WriteUInt8(data, i, (byte)(i * 29 + 7));
        file = DOS.Open("SEQ0:Docs/Payload", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Seek(file.Value, 510, -1) == -1 ||
            DOS.Write(file.Value, data, 7) != 7 || DOS.Close(file.Value) == 0) return 34;
        file = DOS.Open("SEQ0:Docs/Payload", DOS.FileMode.OldFile); if (!file.HasValue) return 35;
        if (DOS.SetFileSize(file.Value, 1601, -1) != 1601 || DOS.Close(file.Value) == 0) return 36;
        if (DOS.DeleteFile("SEQ0:LengthProbe") == 0) return 37;
        file = DOS.Open("SEQ0:CopperWrite", DOS.FileMode.OldFile); if (!file.HasValue) return 38;
        if (DOS.Read(file.Value, data, 8195) != 8195 || DOS.Close(file.Value) == 0) return 39;
        for (var i = 0; i < 8195; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 40;
        return 0;
    }
    private static int VerifyFull(APTR store)
    {
        var data = APTR.FromPointer(store.Raw + 512);
        var file = DOS.Open("SEQ0:CopperWrite", DOS.FileMode.OldFile); if (!file.HasValue) return 76;
        if (DOS.Read(file.Value, data, 8195) != 8195 || DOS.Close(file.Value) == 0) return 77;
        for (var i = 0; i < 8195; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 78;
        file = DOS.Open("SEQ0:CopperDir/Renamed", DOS.FileMode.OldFile); if (!file.HasValue) return 79;
        if (DOS.Read(file.Value, data, 1027) != 1027 || DOS.Close(file.Value) == 0) return 80;
        for (var i = 0; i < 1027; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 17 + 11)) return 81;
        var locked = DOS.Lock("SEQ0:CopperDir/Renamed", DOS.LockMode.Shared); if (!locked.HasValue) return 82;
        var ok = DOS.Examine(locked.Value, data); DOS.UnLock(locked.Value);
        if (ok == 0 || APTR.ReadUInt32(data, FileInfoBlock.ProtectionOffset) != 128 || APTR.ReadUInt32(data, FileInfoBlock.DateDaysOffset) != 1234 ||
            APTR.ReadUInt32(data, FileInfoBlock.DateMinuteOffset) != 5 || APTR.ReadUInt32(data, FileInfoBlock.DateTickOffset) != 100) return 83;
        var comment = APTR.FromPointer(CString.ToUInt32("Written by CopperSFS2"));
        for (var i = 0; i <= 21; i++) if (APTR.ReadUInt8(data, FileInfoBlock.CommentOffset + i) != APTR.ReadUInt8(comment, i)) return 84;
        file = DOS.Open("SEQ0:Docs/Payload", DOS.FileMode.OldFile); if (!file.HasValue) return 85;
        if (DOS.Read(file.Value, data, 1601) != 1601 || DOS.Close(file.Value) == 0) return 86;
        for (var i = 0; i < 1601; i++)
        {
            var expected = i >= 510 && i < 517 ? (byte)((i - 510) * 29 + 7) : i < 1301 ? (byte)(i * 17 + 3) : (byte)0;
            if (APTR.ReadUInt8(data, i) != expected) return 87;
        }
        locked = DOS.Lock("SEQ0:LengthProbe", DOS.LockMode.Shared);
        if (locked.HasValue || DOS.IoErr() != DOS.Error.ObjectNotFound) return 88;
        return 0;
    }
}

public sealed class OriginalSfs2ImageTheoryAttribute : TheoryAttribute
{
    public OriginalSfs2ImageTheoryAttribute(string handlerVariable)
    {
        foreach (var key in new[] { "COPPERSHARP_KICKSTART31_ROM", "COPPERSFS_SFS2_REFERENCE_IMAGE", "COPPERFS_ORIGINAL_SFS", handlerVariable })
            if (!File.Exists(Environment.GetEnvironmentVariable(key))) Skip = $"Supply {key} for original-image SFS2 qualification.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    private const string CsunHandler = "COPPERFS_SFS2_CSUN_HANDLER", TrstHandler = "COPPERFS_SFS2_TRST_HANDLER";

    [OriginalSfs2ImageTheory(CsunHandler)]
    [InlineData(M68kCpuModel.M68000)]
    [InlineData(M68kCpuModel.M68020)]
    public Task OriginalSfs2ImageCsUn(M68kCpuModel cpu) => CheckOriginalImage(cpu, CsunHandler, "sfs2-csun");

    [OriginalSfs2ImageTheory(TrstHandler)]
    [InlineData(M68kCpuModel.M68000)]
    [InlineData(M68kCpuModel.M68020)]
    public Task OriginalSfs2ImageTrst(M68kCpuModel cpu) => CheckOriginalImage(cpu, TrstHandler, "sfs2-trst");

    private async Task CheckOriginalImage(M68kCpuModel cpu, string handlerVariable, string profile)
    {
        var candidate = Environment.GetEnvironmentVariable(handlerVariable)!;
        var original = Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")!;
        Assert.Equal("C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205", Hash(File.ReadAllBytes(original)));
        var initial = File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_REFERENCE_IMAGE")!);
        var directory = Directory.CreateTempSubdirectory("CopperFS-OriginalImage-").FullName;
        try
        {
            await Run(directory, profile, "initial-read", candidate, initial, false, "OriginalSfs2Probe::ReadOriginal", cpu: cpu);
            var written = await Run(directory, profile, "mutate", candidate, initial, true, "OriginalSfs2Probe::MutateOriginal", cpu: cpu);
            using var stream = new MemoryStream(written, writable: false);
            var report = SfsImageChecker.Check(stream, reservedBlocks: 2);
            Assert.Contains(report.Entries, e => e.Path == "/Docs/Payload" && e.Size == 1601);
            Assert.DoesNotContain(report.Entries, e => e.Path == "/LengthProbe");
            var renamed = report.Entries.Single(e => e.Path == "/CopperDir/Renamed");
            Assert.Equal("Written by CopperSFS2", renamed.Comment);
            await Run(directory, profile, "cold-remount", candidate, written, false, "OriginalSfs2Probe::VerifyMutations", cpu: cpu);
            // The original SFS2 reference handler requires a 68020.
            await Run(directory, profile, "original-reader", original, written, false, "OriginalSfs2Probe::VerifyMutations", cpu: M68kCpuModel.M68020);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
