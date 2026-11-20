using CopperFS.Prototype;
using static CopperFS.Prototype.Layout;
using static CopperFS.Prototype.Tests.DesignTests;

namespace CopperFS.Prototype.Tests;

public class AdmissionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Real, consistent full-volume image: transfer the still-allocated reserve run to
    // an existing file's extent tree. This fixture models a previously depleted pool;
    // it never makes unreachable allocations to manufacture the condition.
    internal static byte[] Exhausted()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        Assert.Equal(MutationResult.Success, v.Create("full"u8, out ulong id));
        int length = checked((int)v.Check().FreeUnits * 512); byte[] data = new byte[length];
        MutationResult result = v.Append(id, 0, data, out int written);
        Assert.True(result is MutationResult.Success or MutationResult.DiskFull);
        // The extent-tree promotion consumes a reserve page but no bitmap space.
        var l = v.Geometry; Assert.Equal(0UL, v.Check().FreeUnits);
        Span<byte> reserve = d.Bytes.AsSpan((int)(l.Reserve * 512), l.PageBytes);
        int count = U16(reserve, 64); Assert.True(count >= 12);
        ulong start = U64(reserve, 72);
        for (int i = 0; i < count; i++) Assert.Equal(start + (ulong)i * l.Q, U64(reserve, 72 + i * 8));
        // Root object leaf has the root directory and the one file.
        Span<byte> leaf = d.Bytes.AsSpan((int)(l.DataStart * 512), l.PageBytes), obj = leaf.Slice(64 + 256, 256);
        Assert.Equal(id, U64(obj, 0)); Assert.Equal(2, obj[85]);
        Span<byte> extents = d.Bytes.AsSpan((int)(U64(obj, 24) * 512), l.PageBytes); int n = U16(extents, 48);
        Assert.Equal(0, U16(extents, 50)); Assert.True(n < 40);
        ulong logical = DivideUp(U64(obj, 8), 512);
        Span<byte> extra = extents.Slice(64 + n * 24, 24); extra.Clear(); Put64(extra, 0, logical); Put64(extra, 8, start); Put32(extra, 16, (uint)count * (uint)l.Q);
        Put16(extents, 48, (ushort)(n + 1)); Seal(extents);
        Put64(obj, 8, (logical + (uint)count * l.Q) * 512); Put64(obj, 16, U64(obj, 16) + (uint)count * l.Q); Seal(leaf);
        reserve[64..].Clear(); Seal(reserve);
        var check = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.Equal(0, check.AvailableReservePages); check.Check(); new IndependentImage(d.Bytes);
        return d.Bytes;
    }
    [Fact] public void EmptyReserveAndFullDiskRejectCreateButDeletionNeedsNoMetadataAllocation()
    {
        var d = new MemoryDevice(Exhausted()); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        ulong generation = v.Generation;
        Assert.Equal(MutationResult.ReserveExhausted, v.Create("new"u8, out _));
        Assert.Equal(generation, v.Generation); Assert.False(v.TryGetInfo("new"u8, out _));
        Assert.Equal(MutationResult.Success, v.Delete("full"u8)); Assert.True(v.Check().FreeUnits > 0); new IndependentImage(d.Bytes);
        Assert.Equal(MutationResult.Success, v.Create("after"u8, out _)); Assert.InRange(v.MaximumTransactionPages, 1, 42);
    }
    [Fact] public void ExhaustedReserveCleanupSurvivesEveryCrashBoundary()
    {
        byte[] baseline = Exhausted(); var trace = new CrashDevice(baseline); Delete(trace);
        output.WriteLine($"{trace.Events.Count} exhausted-reserve cuts x 3 persistence schedules = {trace.Events.Count * 3} crash recoveries.");
        // Drop/all/reverse-subset cover queued ownership, chunk frees and tree pruning.
        for (int cut = 1; cut <= trace.Events.Count; cut++) for (int mode = 0; mode < 3; mode++)
        {
            var d = new CrashDevice(baseline) { Cut = cut, Persistence = mode }; Assert.Throws<PowerLoss>(() => Delete(d));
            var finalDevice = new MemoryDevice(d.Durable); var final = new PrototypeVolume<MemoryDevice>(finalDevice, Workspace());
            final.Check(); new IndependentImage(finalDevice.Bytes);
            if (!final.TryGetInfo("full"u8, out _)) Assert.Equal(MutationResult.Success, final.Create("again"u8, out _));
        }
        static void Delete(CrashDevice d) { var v = new PrototypeVolume<CrashDevice>(d, Workspace()); Assert.Equal(MutationResult.Success, v.Delete("full"u8)); }
    }
    [Fact] public void RefillIsSeparateAndFitsSixImages()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        for (int i = 0; i < 30; i++) Assert.Equal(MutationResult.Success, v.Create(System.Text.Encoding.ASCII.GetBytes($"long-{i}-" + new string('a', 100)), out _));
        Assert.True(v.Generation > 30); Assert.InRange(v.MaximumTransactionPages, 1, 39); v.Check(); new IndependentImage(d.Bytes);
    }
    [Theory] [InlineData(7,false)] [InlineData(8,true)]
    public void RealSplitCascadeAndHeightEightAdmission(int height, bool reject)
    {
        var d = Fresh(megabytes: 8); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        var l = v.Geometry; ulong cursor = l.DataStart + 100 * l.Q, key = 1;
        ulong root = Build(height - 1, true); ulong originalRoot = root;
        byte[] record = new byte[24]; Put64(record, 0, key); Put64(record, 8, 5000); Put32(record, 16, 1);
        byte[] before = (byte[])d.Bytes.Clone(); ulong generation = v.Generation;
        var result = v.InsertForQualification(ref root, TreeKind.Extent, 99, record);
        Assert.Equal(reject ? MutationResult.TreeTooDeep : MutationResult.Success, result);
        if (reject)
        {
            Assert.Equal(originalRoot, root); Assert.Equal(generation, v.Generation);
            Assert.Equal(before.AsSpan(2097152).ToArray(), d.Bytes.AsSpan(2097152).ToArray());
            var reopened = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.Equal(generation, reopened.Generation);
        }
        else
        {
            Assert.Equal(16, v.LastTransactionPages); // 2*7+1 tree images plus one reserve image
            Assert.Equal(6, U16(before, checked((int)(originalRoot * 512)) + 50));
            Assert.Equal(7, U16(d.Bytes, checked((int)(root * 512)) + 50));
            Assert.Equal(24, v.AvailableReservePages); // seven siblings and a new root
        }
        // Sparse but structurally valid full right spine. Other children have unary
        // paths of the correct height. It is a tree-algorithm fixture, deliberately
        // detached from the namespace; the full allocation oracle is used elsewhere.
        ulong Build(int level, bool full)
        {
            ulong address = cursor; cursor += l.Q; int count = level == 0 ? (full ? 40 : 1) : (full ? 60 : 1);
            byte[] page = new byte[l.PageBytes]; Header(page, level == 0 ? PageType.ExtentLeaf : PageType.ExtentBranch, address, 99, 0, count, level);
            for (int i = 0; i < count; i++)
            {
                int at = 64 + i * (level == 0 ? 24 : 16); Put64(page, at, key);
                if (level == 0) { Put64(page, at + 8, 5000 + key); Put32(page, at + 16, 1); key++; }
                else Put64(page, at + 8, Build(level - 1, full && i == count - 1));
            }
            Seal(page); page.CopyTo(d.Bytes, checked((int)(address * 512))); return address;
        }
    }
}
