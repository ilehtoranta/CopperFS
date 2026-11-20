using System.Text;
using CopperFS.Prototype;
using static CopperFS.Prototype.Layout;

namespace CopperFS.Prototype.Tests;

public class DesignTests
{
    internal static byte[] Workspace(int allocation = 512) => new byte[PrototypeVolume<MemoryDevice>.RequiredScratchBytes(Math.Max(1024, allocation))];
    internal static MemoryDevice Fresh(int allocation = 512, int sector = 512, int megabytes = 4)
    { var d = new MemoryDevice(sector, megabytes); PrototypeVolume<MemoryDevice>.Format(d, allocation, Workspace(allocation), 12345); return d; }
    internal static byte[] Pattern(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 37 + 19)).ToArray();
    [Fact] public void CrcIsCastagnoli() => Assert.Equal(0xe3069283u, Crc("123456789"u8));
    [Theory] [InlineData(512,512)] [InlineData(1024,512)] [InlineData(2048,512)] [InlineData(4096,512)] [InlineData(4096,4096)]
    public void FormatInlineExtentAndRemount(int allocation, int sector)
    {
        var d = Fresh(allocation, sector); var v = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        Assert.Equal(32, v.AvailableReservePages); Assert.Equal(0UL, v.Check().Files);
        Assert.Equal(MutationResult.Success, v.Put("inline"u8, Pattern(160)));
        var data = Pattern(71001); Assert.Equal(MutationResult.Success, v.Put("extent"u8, data));
        Assert.True(v.TryGetInfo("EXTENT"u8, out var info)); Assert.False(info.IsInline);
        byte[] actual = new byte[data.Length]; Assert.Equal(data.Length, v.Read(info.Id, 0, actual)); Assert.Equal(data, actual);
        var report = v.Check(); Assert.Equal(2UL, report.Files);
        var reopened = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation)); Assert.Equal(report, reopened.Check());
        var oracle = new IndependentImage(d.Bytes); Assert.Equal(data, oracle.Read(info.Id));
        Assert.InRange(v.MaximumTransactionPages, 1, 42);
    }
    [Fact] public void Latin1NamesAreStoredAsUtf8AndFoldedWithoutGuessing()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        byte[] latin = [0xc4, 0xe9]; Assert.Equal(MutationResult.Success, v.Create(latin, out ulong id));
        Assert.True(v.TryGetInfo(new byte[] { 0xe4, 0xe9 }, out var found)); Assert.Equal(id, found.Id);
        Assert.Equal(MutationResult.Success, v.Create(new byte[] { 0xc3, 0xa4, 0xe9 }, out _));
        Assert.Equal(MutationResult.Success, v.Create(Enumerable.Repeat((byte)0xe9, 255).ToArray(), out _));
        Assert.Equal(MutationResult.InvalidName, v.Create("__u0000000000000001"u8, out _));
        Assert.Equal(MutationResult.InvalidName, v.Create("__U0000000000000001"u8, out _));
        Assert.Equal(MutationResult.Success, v.Create("__unreserved"u8, out _));
        var oracle = new IndependentImage(d.Bytes); Assert.Contains((1UL, "äé"), oracle.Names.Keys);
        Assert.Contains((1UL, new string('é', 255)), oracle.Names.Keys); v.Check();
    }
    [Fact] public void SplitsDirectoriesObjectsAndExtentsThenReclaimsAllOwnership()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        for (int i = 0; i < 90; i++) Assert.Equal(MutationResult.Success, v.Put(Encoding.ASCII.GetBytes($"f{i:D3}"), Pattern(i % 161)));
        Assert.Equal(MutationResult.Success, v.Create("fragmented"u8, out ulong id));
        for (int i = 0; i < 90; i++)
        { Assert.Equal(MutationResult.Success, v.Append(id, (ulong)i * 1024, Pattern(512), out int written)); Assert.Equal(512, written); }
        byte[] payload = new byte[90 * 1024 - 512]; Assert.Equal(payload.Length, v.Read(id, 0, payload));
        for (int i = 0; i < 90; i++) Assert.Equal(Pattern(512), payload.AsSpan(i * 1024, 512).ToArray());
        new IndependentImage(d.Bytes); v.Check();
        Assert.Equal(MutationResult.Success, v.Delete("fragmented"u8));
        for (int i = 0; i < 90; i++) Assert.Equal(MutationResult.Success, v.Delete(Encoding.ASCII.GetBytes($"f{i:D3}")));
        Assert.Equal(0UL, v.Check().Files); Assert.Empty(new IndependentImage(d.Bytes).Names);
    }
    [Fact] public void SubdirectoriesAndCaseOnlyRename()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        Assert.Equal(MutationResult.Success, v.Create(1, "sub"u8, true, out ulong sub));
        Assert.Equal(MutationResult.Success, v.Create(sub, "child"u8, false, out _));
        Assert.Equal(MutationResult.DirectoryNotEmpty, v.Delete("sub"u8));
        Assert.Equal(MutationResult.Success, v.Rename("sub"u8, "SUB"u8));
        Assert.Equal(MutationResult.Success, v.Delete(sub, "child"u8)); Assert.Equal(MutationResult.Success, v.Delete("sub"u8));
        v.Check(); new IndependentImage(d.Bytes);
    }
    [Fact] public void TrimPoisonsOnlyRetiredFreeSpaceAndNewDataIsZeroInitialized()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        Assert.Equal(MutationResult.Success, v.Put("keep"u8, Pattern(3000)));
        Assert.True(v.Trim() > 0); Assert.NotEmpty(d.Discarded);
        Assert.Equal(MutationResult.Success, v.Create("sparse"u8, out ulong id));
        Assert.Equal(MutationResult.Success, v.Append(id, 10000, new byte[] { 7 }, out _));
        byte[] result = new byte[10001]; v.Read(id, 0, result); Assert.All(result[..10000], b => Assert.Equal(0, b)); Assert.Equal(7, result[^1]);
        new IndependentImage(d.Bytes); v.Check();
    }
    [Fact] public void SuccessfulOperationsUseOnlyCallerOwnedScratch()
    {
        var d = Fresh(); byte[] work = Workspace(); var v = new PrototypeVolume<MemoryDevice>(d, work);
        v.Put("warm"u8, "hello"u8); v.Delete("warm"u8); v.Check();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var reopened = new PrototypeVolume<MemoryDevice>(d, work);
        var result = reopened.Put("file"u8, "hello"u8); Span<byte> data = stackalloc byte[5]; reopened.Read("file"u8, 0, data);
        reopened.Rename("file"u8, "rename"u8); reopened.Delete("rename"u8); reopened.Check();
        long after = GC.GetAllocatedBytesForCurrentThread(); Assert.Equal(before, after); Assert.Equal(MutationResult.Success, result);
    }
    [Fact] public void Utf8ImportUsesStableAliasesOnTheLegacyInterface()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        Assert.Equal(MutationResult.Success, v.CreateUtf8(1, Encoding.UTF8.GetBytes("κόσμος"), false, out ulong id));
        Assert.Equal(MutationResult.InvalidName, v.CreateUtf8(1, new byte[] { 0xc0, 0xaf }, false, out _));
        var cursor = new DirectoryCursor(); Span<byte> name = stackalloc byte[108];
        Assert.True(v.TryEnumerateLegacy(1, ref cursor, name, out int length, out var info)); Assert.Equal(id, info.Id);
        Assert.Equal(19, length); Assert.True(v.TryGetInfo(name[..length], out info)); Assert.Equal(id, info.Id);
        Assert.Equal(MutationResult.Success, v.Delete(name[..length])); new IndependentImage(d.Bytes);
    }
    [Fact] public void CrossDirectoryReplacementHardLinksAndAncestry()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        v.Create(1, "a"u8, true, out ulong a); v.Create(1, "b"u8, true, out ulong b);
        v.Create(a, "source"u8, false, out ulong id); v.Append(id, 0, Pattern(2000), out _);
        Assert.Equal(MutationResult.Success, v.Link(id, 1, "link"u8));
        v.Create(b, "victim"u8, false, out _);
        Assert.Equal(MutationResult.Success, v.Rename(a, "source"u8, b, "victim"u8, true));
        Assert.True(v.TryGetInfo(b, "victim"u8, out var info)); Assert.Equal(id, info.Id);
        Assert.Equal(MutationResult.InvalidOperation, v.Rename(1, "a"u8, a, "cycle"u8));
        Assert.Equal(MutationResult.Success, v.Delete(b, "victim"u8)); Assert.True(v.TryGetInfo("link"u8, out info));
        Assert.Equal(Pattern(2000), new IndependentImage(d.Bytes).Read(info.Id)); v.Check();
    }
    [Fact] public void TruncateAndExtendNeverExposeOldTailOrFreedUnits()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace()); v.Put("file"u8, Pattern(100000)); v.TryGetInfo("file"u8, out var info);
        Assert.Equal(MutationResult.Success, v.Truncate(info.Id, 103)); Assert.Equal(MutationResult.Success, v.Truncate(info.Id, 50000));
        byte[] actual = new byte[50000]; v.Read(info.Id, 0, actual); Assert.Equal(Pattern(103), actual[..103]); Assert.All(actual[103..], b => Assert.Equal(0, b));
        Assert.Equal(MutationResult.Success, v.Truncate(info.Id, 0)); v.Check(); new IndependentImage(d.Bytes);
        Assert.Equal(MutationResult.Success, v.Append(info.Id, 0, "new"u8, out _)); v.Check();
    }
    [Fact] public void UnwrittenMiddleWriteSplitsTwiceAndPromotesTheExtentTree()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace()); v.Trim(); v.Create("file"u8, out ulong id);
        Assert.Equal(MutationResult.Success, v.Preallocate(id, 0, 12, out uint allocated)); Assert.Equal(12u, allocated);
        for (uint i = 1; i <= 5; i++) Assert.Equal(MutationResult.Success, v.Preallocate(id, i * 100 * 512, 1, out _));
        Assert.True(v.TryGetInfo("file"u8, out var before)); Assert.Equal(6UL, before.ExtentCount);
        Assert.Equal(MutationResult.Success, v.Write(id, 512 + 7, Pattern(1301), out int written)); Assert.Equal(1301, written);
        Assert.True(v.TryGetInfo("file"u8, out var after)); Assert.Equal(8UL, after.ExtentCount);
        var oracle = new IndependentImage(d.Bytes); byte[] data = oracle.Read(id); Assert.Equal(Pattern(1301), data.AsSpan(519, 1301).ToArray());
        Assert.All(data[..519], b => Assert.Equal(0, b)); Assert.All(data[1820..], b => Assert.Equal(0, b));
        Assert.Equal(MutationResult.Success, v.Write(id, 50 * 512, "hole"u8, out _));
        Assert.Equal(MutationResult.Success, v.Write(id, 600, "overwrite"u8, out _)); v.Check(); new IndependentImage(d.Bytes);
    }
    [Fact] public void EqualCrcNamesStillCompareTheirCompleteSpelling()
    {
        Dictionary<uint, byte[]> seen = []; byte[]? a = null, b = null; ulong random = 123;
        for (int i = 0; i < 250000; i++)
        {
            random ^= random << 13; random ^= random >> 7; random ^= random << 17;
            byte[] name = Encoding.ASCII.GetBytes($"n{random:x16}"); uint hash = Crc(name);
            if (seen.TryGetValue(hash, out a)) { b = name; break; } seen[hash] = name;
        }
        Assert.NotNull(a); Assert.NotNull(b); Assert.False(a.AsSpan().SequenceEqual(b));
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        Assert.Equal(MutationResult.Success, v.Create(a, out ulong first)); Assert.Equal(MutationResult.Success, v.Create(b, out ulong second));
        Assert.NotEqual(first, second); Assert.True(v.TryGetInfo(a, out var info)); Assert.Equal(first, info.Id);
        Assert.True(v.TryGetInfo(b, out info)); Assert.Equal(second, info.Id); v.Check(); new IndependentImage(d.Bytes);
    }
    [Fact] public void FailedOwnershipCheckDisablesDiscardAndFurtherMutation()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        var l = v.Geometry; Span<byte> bitmap = d.Bytes.AsSpan((int)(l.Bitmap(0, 1) * 512), 1024);
        bitmap[64 + 200 / 8] |= (byte)(128 >> (200 % 8)); Seal(bitmap);
        try { v.Check(); Assert.Fail("Accepted leaked unit."); } catch (InvalidDataException) { }
        try { v.Trim(); Assert.Fail("Trim after failed checker."); } catch (InvalidOperationException) { }
        try { v.Create("unsafe"u8, out _); Assert.Fail("Mutation after failed checker."); } catch (InvalidOperationException) { }
        Assert.Equal(0, d.Discards);
    }
    [Theory] [InlineData("write")] [InlineData("append")] [InlineData("preallocate")]
    public void SparseGrowthAfterShrinkZerosTheRetainedPartialUnit(string operation)
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace()); v.Put("file"u8, Pattern(5000)); v.TryGetInfo("file"u8, out var info);
        Assert.Equal(MutationResult.Success, v.Truncate(info.Id, 103));
        MutationResult result = operation == "append" ? v.Append(info.Id, 2000, "end"u8, out _) : operation == "write" ? v.Write(info.Id, 2000, "end"u8, out _) : v.Preallocate(info.Id, 2048, 1, out _);
        Assert.Equal(MutationResult.Success, result); byte[] data = new IndependentImage(d.Bytes).Read(info.Id);
        Assert.Equal(Pattern(103), data[..103]); Assert.All(data[103..2000], b => Assert.Equal(0, b));
        if (operation == "preallocate") Assert.All(data[2000..], b => Assert.Equal(0, b)); else Assert.Equal("end"u8.ToArray(), data[2000..]); v.Check();
    }
}
