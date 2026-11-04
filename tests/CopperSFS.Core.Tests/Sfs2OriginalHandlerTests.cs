using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;
using Task = System.Threading.Tasks.Task;
using Process = System.Diagnostics.Process;

namespace CopperSFS.Tests;

public sealed class Sfs2NativeRedoFactAttribute : FactAttribute
{
    public Sfs2NativeRedoFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_NATIVE_REDO_IMAGE")))
            Skip = "Requires the original-handler capture-redo fixture; see docs/research-port.md.";
    }
}
public sealed class Sfs2OriginalHandlerTests(ITestOutputHelper output)
{
    [Sfs2NativeRedoFact]
    public void CopperSfs2RecoversAnActualOriginalHandlerRedoTransaction()
    {
        var source = File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_NATIVE_REDO_IMAGE")!);
        var disk = new StudyDisk(source, 512, reservedBlocks: 2); var w = WorkFor(disk);
        Assert.False(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, w.Meta, out _));
        Assert.True(Recover(ref disk, ref w), w.Error.ToString());
        var report = Inspect(disk.Snapshot(), reserved: 2);
        Assert.Contains(report.Entries, e => e.Path == "/Docs/Payload" && e.Size == 1301);
        Assert.Contains(report.Entries, e => e.Path == "/Docs/Entry79");
        Assert.Contains(report.Entries, e => e.Path == "/LengthProbe" && e.Size == 513);
        var recovered = disk.Snapshot(); Assert.True(Recover(ref disk, ref w)); Assert.Equal(recovered, disk.Snapshot());
        output.WriteLine("Original SFS2 TRST capture recovered, independently checked, and repeat recovery was byte-identical.");
    }

}
