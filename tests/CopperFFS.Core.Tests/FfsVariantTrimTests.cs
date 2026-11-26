using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    // Manual, tracked and automatic TRIM on all six classic variants. The
    // international variants only change name hashing; DOS4/5 directory-cache
    // blocks are allocated in the bitmap, so the scan must leave them, every
    // header and every data block alone. DiscardBlocks itself asserts that
    // each discarded block is free on disk.
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void TrimPreservesEveryVariantsFilesAndDirectoryCaches(int dos)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(9003, 512, 2, out var g));
        var p = new Memory(storage, g); var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        storage.FreedQueue = APTR.FromPointer((uint)storage.Ram.Length - 4096);
        var drawer = VariantCreate(ref p, storage, g.RootBlock, "Drawer", true);
        var kept = new List<(AmigaFfsWriteCore.Request File, byte[] Data)>();
        for (var i = 0; i < 12; i++)
        {
            var file = VariantCreate(ref p, storage, drawer.Header, $"File{i:D2}" + new string('n', 20));
            var data = Enumerable.Range(0, 3000 + i * 97).Select(n => (byte)(n * 3 + i)).ToArray();
            OfsWrite(ref p, storage, ref file, 0, data);
            kept.Add((file, data));
        }
        // Free some data, headers and (on DOS4/5) a whole cache block.
        foreach (var (file, _) in kept.Where((_, i) => i % 3 == 0).ToArray())
        {
            var w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, false), w.Error.ToString());
        }
        kept = kept.Where((_, i) => i % 3 != 0).ToList();
        // Tracked hints, as the automatic queue collects them.
        while (AmigaFreedRangeQueue.Take(ref p, storage.FreedQueue, 4096, 0, out var first, out var count))
        {
            var w = Work();
            Assert.True(AmigaFfsWriteCore.TrimFreedRange(ref p, ref w, APTR.Null, first, count, out _), w.Error.ToString());
        }
        var tracked = storage.Discarded;
        Assert.True(tracked > 0);
        // A manual whole-volume TRIM.
        var manual = Work();
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref manual, APTR.Null, 0, out var result), manual.Error.ToString());
        Assert.True(result.Blocks > 0);
        // An automatic batch over the first free blocks after the files.
        var batch = AutomaticBatch(ref p, storage, (g.ReservedBlocks, 64));
        var auto = Work(); var ok = true; var done = false;
        for (var turn = 0; ok && !done && turn < 16; turn++)
            ok = AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref auto, batch, 11, 7, out done);
        Assert.True(ok && done, auto.Error.ToString());
        foreach (var (file, data) in kept)
        {
            Assert.Equal(data.Length, AmigaDiskFileSystemCore.Read(ref p, file.Header, type, 0, Output, (uint)data.Length, Meta, Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(data, storage.Ram.AsSpan((int)Output.Raw, data.Length).ToArray());
        }
        if (dos >= 4)
        {
            InspectDirectoryCache(storage, g, drawer.Header, kept.Count);
            InspectDirectoryCache(storage, g, g.RootBlock, 1);
        }
        Encoding.Latin1.GetBytes("Drawer/" + "File01" + new string('n', 20)).CopyTo(storage.Ram, (int)Name.Raw);
        Assert.Equal(kept[0].File.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 33, type, Meta, out _));
    }
}
