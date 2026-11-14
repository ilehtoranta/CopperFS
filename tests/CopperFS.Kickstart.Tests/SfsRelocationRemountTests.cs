using System.Text.Json;
using Amiga;
using Copper68k;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public static class SfsRelocationRemountProbe
{
    public static int Seed() => Run(0);
    public static int Write() => Run(1);
    public static int Verify() => Run(2);

    private static int Run(int operation)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return Fail();
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(global::Amiga.Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(8192, global::Amiga.Exec.MemoryFlags.Public));
        if (control.IsNull || data.IsNull || CorrectnessProbe.Mount(control, 2) != 0) return Fail();
        var port = DOS.DeviceProc("SEQ0:");
        if (operation == 0)
        {
            var file = DOS.Open("SEQ0:Relocate", DOS.FileMode.NewFile);
            if (!file.HasValue) return Fail();
            for (int chunk = 0; chunk < 4; chunk++)
            {
                for (int i = 0; i < 8192; i++) APTR.WriteUInt8(data, i, (byte)(i * 13 + chunk));
                if (DOS.Write(file.Value, data, 8192) != 8192) return Fail();
            }
            if (DOS.Close(file.Value) == 0) return Fail();
            var neighbor = DOS.Open("SEQ0:Neighbor", DOS.FileMode.NewFile);
            if (!neighbor.HasValue || DOS.Write(neighbor.Value, data, 512) != 512 ||
                DOS.Close(neighbor.Value) == 0 || DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return Fail();
        }
        else if (operation == 1)
        {
            var file = DOS.Open("SEQ0:Relocate", DOS.FileMode.ReadWrite);
            if (!file.HasValue || DOS.Seek(file.Value, 8192, -1) != 0) return Fail();
            for (int chunk = 1; chunk <= 2; chunk++)
            {
                for (int i = 0; i < 8192; i++) APTR.WriteUInt8(data, i, (byte)(0xD0 + chunk));
                if (DOS.Write(file.Value, data, 8192) != 8192) return Fail();
            }
            if (DOS.Close(file.Value) == 0 || DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return Fail();
        }
        else
        {
            var file = DOS.Open("SEQ0:Relocate", DOS.FileMode.OldFile);
            if (!file.HasValue) return Fail();
            for (int chunk = 0; chunk < 4; chunk++)
            {
                if (DOS.Read(file.Value, data, 8192) != 8192) return Fail();
                for (int i = 0; i < 8192; i++)
                    if (APTR.ReadUInt8(data, i) != (chunk == 1 || chunk == 2 ? (byte)(0xD0 + chunk) : (byte)(i * 13 + chunk))) return Fail();
            }
            if (DOS.Read(file.Value, data, 1) != 0 || DOS.Close(file.Value) == 0) return Fail();
        }
        CorrectnessProbe.Report(210);
        return 0;
    }

    private static int Fail() { CorrectnessProbe.Report(250); return 1; }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [CorrectnessTheory]
    [InlineData("sfs")]
    public async System.Threading.Tasks.Task RelocationAt8192SurvivesFlushAndColdRemount(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "relocation-8192");
        var seed = File.ReadAllBytes(manifest.ClassicBase);
        Assert.Equal(manifest.ClassicBaseSha256, Hash(seed));
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var prepared = await Run(root, profile, "seed", pair.Original, seed, true,
            "SfsRelocationRemountProbe::Seed", cpu: M68kCpuModel.M68020);
        var written = await Run(root, profile, "write", pair.Candidate, prepared, true,
            "SfsRelocationRemountProbe::Write", cpu: M68kCpuModel.M68000);
        var files = SfsImageChecker.Check(new MemoryStream(written), reservedBlocks: 2).Entries;
        var expected = Enumerable.Range(0, 32768).Select(i => (byte)(13 * i + i / 8192)).ToArray();
        var neighbor = expected.AsSpan(24576, 512).ToArray();
        Array.Fill(expected, (byte)0xD1, 8192, 8192);
        Array.Fill(expected, (byte)0xD2, 16384, 8192);
        Assert.Equal(Hash(expected), files.Single(e => e.Path == "/Relocate").Sha256);
        Assert.Equal(Hash(neighbor), files.Single(e => e.Path == "/Neighbor").Sha256);
        await Run(root, profile, "remount", pair.Candidate, written, false,
            "SfsRelocationRemountProbe::Verify", cpu: M68kCpuModel.M68000);
    }
}
