using System.Text;
using Amiga;

namespace CopperSFS.Bench;

internal static class FsOps
{
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    internal static uint Create(ref StudyDisk disk, string name, bool directory = false)
    {
        var work = disk.Work;
        Encoding.Latin1.GetBytes(name).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.NameBuffer, Length = (uint)name.Length };
        var ok = AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, directory);
        if (!ok) throw new InvalidOperationException($"Create {name}: {work.Error}");
        return request.Node;
    }
    internal static void Write(ref StudyDisk disk, uint node, uint position, byte[] data)
    {
        data.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        WriteFromRam(ref disk, node, position, data.Length);
    }
    internal static void WriteFromRam(ref StudyDisk disk, uint node, uint position, int count, int bufferOffset = 0)
    {
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = node, Position = position, Length = (uint)count, Buffer = APTR.FromPointer(disk.Payload.Raw + (uint)bufferOffset) };
        uint actual;
        var ok = disk.BatchedWrites
            ? AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, disk.BatchBuffer, disk.BatchBytes, out actual, disk.RelocateFragmented)
            : AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out actual);
        if (!ok || actual != count) throw new InvalidOperationException($"Write {node} at {position}: {work.Error}, {actual}/{count}");
    }
    internal static void Read(ref StudyDisk disk, uint node, uint position, int count, int bufferOffset = 0)
    {
        var work = disk.Work;
        var actual = AmigaSmartFileSystemCore.Read(ref disk, node, position,
            APTR.FromPointer(disk.Payload.Raw + (uint)bufferOffset), (uint)count, work.Meta, work.Data, out var error);
        if (actual != count) throw new InvalidOperationException($"Read {node} at {position}: {error}, {actual}/{count}");
    }
    internal static uint Resolve(ref StudyDisk disk, string name)
    {
        Encoding.Latin1.GetBytes(name).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var node = AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.NameBuffer, (uint)name.Length, disk.Work.Meta, out var error);
        if (node == 0) throw new InvalidOperationException($"Resolve {name}: {error}");
        return node;
    }
    internal static void Rename(ref StudyDisk disk, uint node, string name)
    {
        var work = disk.Work;
        Encoding.Latin1.GetBytes(name).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = node, Directory = 1, Name = disk.NameBuffer, Length = (uint)name.Length };
        var ok = AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request);
        if (!ok) throw new InvalidOperationException($"Rename {node}: {work.Error}");
    }
    internal static void Resize(ref StudyDisk disk, uint node, uint length)
    {
        var work = disk.Work;
        var ok = AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, node, length);
        if (!ok) throw new InvalidOperationException($"Resize {node}: {work.Error}");
    }
    internal static void Remove(ref StudyDisk disk, uint node)
    {
        var work = disk.Work;
        var ok = AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, node);
        if (!ok) throw new InvalidOperationException($"Remove {node}: {work.Error}");
    }
    internal static void Recover(ref StudyDisk disk)
    {
        var work = disk.Work;
        var ok = AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work);
        if (!ok) throw new InvalidOperationException($"Recover: {work.Error}");
        Require(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta), "Recovered image did not mount");
    }
}
