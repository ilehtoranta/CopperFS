using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    // A write-back device may reorder writes between barriers. The root's
    // valid flag vouches for every bitmap and header write of the operation,
    // so it must not be issued until those writes are flushed.
    [Theory]
    [InlineData(1, false)] [InlineData(0, false)] [InlineData(5, false)]
    [InlineData(1, true)] [InlineData(0, true)]
    public void RootValidWriteFollowsAFlushOfTheOperationsWrites(int dos, bool indexed)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        var w = Work();
        if (indexed)
        {
            w.Index = APTR.FromPointer(0x100000);
            AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        }
        var root = g.StartByte + (ulong)g.RootBlock * g.BlockBytes;
        void Check(string operation, bool succeeded)
        {
            Assert.True(succeeded, operation + ": " + w.Error);
            var order = storage.WriteOrder;
            var last = order.FindLastIndex(e => !e.Flush && e.Offset == root);
            Assert.True(last >= 0, operation + " wrote no root");
            Assert.Equal(last, order.FindLastIndex(e => !e.Flush));
            // Every other write of this operation precedes a barrier that
            // precedes the validating root write.
            var barrier = order.FindLastIndex(last, e => e.Flush);
            Assert.True(barrier >= 0, operation + " validated the root without a flush");
            Assert.DoesNotContain(order.Skip(barrier + 1).Take(last - barrier - 1), e => !e.Flush);
            Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
            storage.WriteOrder.Clear();
        }
        Encoding.Latin1.GetBytes("Drawer").CopyTo(storage.Ram, (int)Name.Raw);
        var dir = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 6 };
        storage.WriteOrder.Clear();
        Check("mkdir", AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        Encoding.Latin1.GetBytes("Drawer/File").CopyTo(storage.Ram, (int)Name.Raw);
        var file = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 11 };
        Check("create", AmigaFfsWriteCore.Create(ref p, ref w, ref file, false));
        for (var i = 0; i < 3000; i++) storage.Ram[(int)Payload.Raw + i] = (byte)(i * 13);
        file.Buffer = Payload; file.Length = 3000; file.Position = 0;
        Check("write", AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _));
        Encoding.Latin1.GetBytes("Moved").CopyTo(storage.Ram, (int)Name.Raw);
        file.Directory = g.RootBlock; file.Name = Name; file.Length = 5;
        Check("rename", AmigaFfsWriteCore.Rename(ref p, ref w, ref file));
        Encoding.Latin1.GetBytes("note").CopyTo(storage.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Length = 4;
        Check("comment", AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref file, DosPacketAction.SetComment));
        Check("delete", AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, false));
        Check("rmdir", AmigaFfsWriteCore.Remove(ref p, ref w, dir.Header, false));
    }
}
