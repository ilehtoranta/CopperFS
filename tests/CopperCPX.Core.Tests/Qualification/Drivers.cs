using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using System.Text;

namespace CopperCPX.Qualification;

internal abstract class ImageDriver(int unit) : IScenarioDriver
{
    protected readonly Tests.Disk Disk = Tests.EngineTests.Fresh(unit);
    protected readonly int Unit = unit;
    public abstract string Name { get; }
    public abstract Answer Execute(Operation operation);
    public virtual void Dispose() { }
    public virtual void Verify(IReadOnlyDictionary<string, byte[]> files)
    {
        var image = new IndependentImage(Disk.Bytes);
        Assert.Equal(files.Keys.Order(), image.Names.Keys.Select(x => x.Name).Order());
        foreach (var (name, data) in files)
        {
            Assert.True(image.Names.TryGetValue((1, name), out ulong id));
            Assert.Equal((ulong)data.Length, IndependentImage.L(image.Objects[id], 8));
            Assert.Equal(data, image.Read(id));
            Answer read = Execute(new(ActionKind.Read, name, Offset: 0, Data: new byte[data.Length + 1]));
            Assert.Equal(Outcome.Success, read.Outcome); Assert.Equal(data, read.Data);
        }
        // A fourth reader is useful but not an oracle vote: each check must pass.
        var reference = new Cpx4Volume<Tests.Disk>(Disk, new byte[36864]); reference.Check();
    }
    protected static byte[] NameBytes(string name) => Encoding.Latin1.GetBytes(name);
}

internal sealed class ReferenceDriver(int unit) : ImageDriver(unit)
{
    public override string Name => "reference";
    public override Answer Execute(Operation op)
    {
        var v = new Cpx4Volume<Tests.Disk>(Disk, new byte[36864]);
        if (op.Kind == ActionKind.Remount) { v.Check(); return new(Outcome.Success); }
        bool exists = v.TryGetInfo(NameBytes(op.Name), out var info);
        if (op.Kind != ActionKind.Create && !exists) return new(Outcome.Missing);
        MutationResult result;
        switch (op.Kind)
        {
            case ActionKind.Create: result = exists ? v.Truncate(info.Id, 0) : v.Create(NameBytes(op.Name), out _); break;
            case ActionKind.Write:
                result = v.Write(info.Id, (uint)op.Offset, op.Data!, out int written);
                if (result == MutationResult.Success) Assert.Equal(op.Data!.Length, written); break;
            case ActionKind.Read:
                byte[] data = new byte[op.Data!.Length]; int count = v.Read(info.Id, (uint)op.Offset, data);
                return new(Outcome.Success, data[..count]);
            case ActionKind.Resize: result = v.Truncate(info.Id, (uint)op.Offset); break;
            case ActionKind.Delete: result = v.Delete(NameBytes(op.Name)); break;
            case ActionKind.Rename: result = v.Rename(NameBytes(op.Name), NameBytes(op.Target)); break;
            case ActionKind.HardLink: result=v.Link(info.Id,1,NameBytes(op.Target)); break;
            default: throw new InvalidDataException();
        }
        return result switch { MutationResult.Success => new(Outcome.Success), MutationResult.AlreadyExists => new(Outcome.Exists), _ => throw new InvalidDataException($"Reference returned {result}") };
    }
}

internal sealed class EngineDriver : ImageDriver
{
    private Tests.Ram ram = new(new byte[160000]);
    private CpxEngine<Tests.Ram> engine;
    public override string Name => "engine";
    internal EngineDriver(int unit) : base(unit) => Mount();
    private void Mount() { ram = new(new byte[160000]); engine = Tests.FileMutationTests.Mount(Disk, ram, Unit); }
    private CpxError Drive(CpxError admission)
    {
        if (admission != CpxError.None) return admission;
        for (int n = 0; n < 100000; n++)
        {
            engine = new(ram, 32); var step = engine.Step();
            if (step == StepResult.Failed) return engine.Error;
            if (step == StepResult.Complete) return CpxError.None;
            Assert.InRange(engine.TransactionImageCount, 0u, 42u);
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request;
            // Exercise arbitrary delayed completion and admission while waiting.
            for (int delay = 0; delay < n % 5; delay++)
            {
                Assert.Equal(StepResult.NeedIo, engine.Step()); Assert.Equal(io, engine.Request);
                Assert.Equal(CpxError.Busy, engine.BeginOperation(CpxOperation.Flush));
            }
            Service(io, ram, Disk); Assert.Equal(CpxError.None, engine.CompleteIo(io.Token, io.Bytes, true));
        }
        throw new InvalidDataException("Engine failed to yield a terminal result within the scenario limit.");
    }
    internal static void Service(CpxIo io, Tests.Ram ram, Tests.Disk disk)
    {
        ulong sector = ((ulong)io.Sector.High << 32) | io.Sector.Low;
        if (io.Kind == IoKind.Read) disk.Read(sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
        else if (io.Kind == IoKind.Write) disk.Write(sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
        else if (io.Kind == IoKind.QueryDiscard) ram.Write32(io.Buffer,4096);
        else if (io.Kind == IoKind.Flush) disk.Flush();
        else if (io.Kind == IoKind.Discard) disk.Bytes.AsSpan(checked((int)(sector * 512)), (int)io.Bytes).Fill(0xa5);
        else throw new InvalidDataException($"Unhandled request {io.Kind}");
    }
    public override Answer Execute(Operation op)
    {
        if (op.Kind == ActionKind.Remount) { Mount(); return new(Outcome.Success); }
        byte[] name = NameBytes(op.Name); name.CopyTo(ram.Bytes, 90000);
        Assert.Equal(CpxError.None, Drive(engine.BeginLookup(Wide.From(1), 90000, (uint)name.Length)));
        bool exists = engine.LookupFound; Wide id = engine.LookupId;
        if (!exists && op.Kind != ActionKind.Create) return new(Outcome.Missing);
        CpxError result;
        switch (op.Kind)
        {
            case ActionKind.Create: result = Drive(exists ? engine.BeginResize(id, default) : engine.BeginCreate(Wide.From(1), 90000, (uint)name.Length)); break;
            case ActionKind.Write:
                op.Data!.CopyTo(ram.Bytes, 100000);
                result = Drive(engine.BeginMappedWrite(id, Wide.From((uint)op.Offset), 100000, (uint)op.Data.Length));
                if (result == CpxError.None) Assert.Equal((uint)op.Data.Length, engine.TransferredBytes); break;
            case ActionKind.Read:
                result = Drive(engine.BeginFileRead(id, Wide.From((uint)op.Offset), 100000, (uint)op.Data!.Length));
                Assert.Equal(CpxError.None, result); return new(Outcome.Success, ram.Bytes.AsSpan(100000, (int)engine.TransferredBytes).ToArray());
            case ActionKind.Resize: result = Drive(engine.BeginResize(id, Wide.From((uint)op.Offset))); break;
            case ActionKind.Delete: result = Drive(engine.BeginDelete(Wide.From(1), 90000, (uint)name.Length)); break;
            case ActionKind.Rename:
                byte[] target = NameBytes(op.Target); target.CopyTo(ram.Bytes, 91000);
                result = Drive(engine.BeginRename(Wide.From(1), 90000, (uint)name.Length, Wide.From(1), 91000, (uint)target.Length)); break;
            case ActionKind.HardLink:
                byte[] alias=NameBytes(op.Target); alias.CopyTo(ram.Bytes,91000);
                result=Drive(engine.BeginHardLink(id,Wide.From(1),91000,(uint)alias.Length)); break;
            default: throw new InvalidDataException();
        }
        return result switch { CpxError.None => new(Outcome.Success), CpxError.AlreadyExists => new(Outcome.Exists), _ => throw new InvalidDataException($"Engine returned {result}") };
    }
}

internal sealed class DispatcherDriver : ImageDriver
{
    internal Tests.Ram Ram = new(new byte[160000]);
    internal CpxDosDispatcher<Tests.Ram> Dispatcher;
    internal const uint PacketAddress = 80000, FileHandle = 81000, NameAddress = 82000, TargetAddress = 82500, DataAddress = 100000;
    public override string Name => "dispatcher";
    internal DispatcherDriver(int unit) : base(unit) => Mount();
    private void Mount()
    {
        Ram = new(new byte[160000]); Tests.FileMutationTests.Mount(Disk, Ram, Unit);
        Dispatcher = new(Ram, 32); Dispatcher.Initialize(0x1234, 0x5678);
    }
    internal (uint Result, uint Error) Packet(uint action, uint a = 0, uint b = 0, uint c = 0, uint d = 0, uint e = 0)
    {
        Ram.Write32(PacketAddress + 8, action); Ram.Write32(PacketAddress + 20, a); Ram.Write32(PacketAddress + 24, b);
        Ram.Write32(PacketAddress + 28, c); Ram.Write32(PacketAddress + 32, d); Ram.Write32(PacketAddress + 36, e);
        Assert.Equal(CpxError.None, Dispatcher.BeginPacket(PacketAddress));
        for (int n = 0; n < 100000; n++)
        {
            Dispatcher = new(Ram, 32); var step = Dispatcher.Step();
            if (step == StepResult.Complete)
            {
                Assert.Equal(Dispatcher.Result, Ram.Read32(PacketAddress + 12)); Assert.Equal(Dispatcher.DosError, Ram.Read32(PacketAddress + 16));
                Assert.False(Dispatcher.Busy); return (Dispatcher.Result, Dispatcher.DosError);
            }
            Assert.NotEqual(StepResult.Failed, step);
            if (step != StepResult.NeedIo) continue;
            var io = Dispatcher.Request;
            for (int delay = 0; delay < n % 5; delay++)
            {
                Assert.Equal(StepResult.NeedIo, Dispatcher.Step()); Assert.Equal(io, Dispatcher.Request);
                Assert.Equal(CpxError.Busy, Dispatcher.BeginPacket(PacketAddress));
            }
            EngineDriver.Service(io, Ram, Disk); Assert.Equal(CpxError.None, Dispatcher.CompleteIo(io.Token, io.Bytes, true));
        }
        throw new InvalidDataException("Packet failed to terminate within the scenario limit.");
    }
    internal uint Bstr(string text, uint address = NameAddress)
    {
        byte[] bytes = NameBytes(text); Assert.InRange(bytes.Length, 0, 255); Ram.Write8(address, (byte)bytes.Length); bytes.CopyTo(Ram.Bytes, (int)address + 1); return address >> 2;
    }
    internal byte[] Image => Disk.Bytes;
    public override Answer Execute(Operation op)
    {
        if (op.Kind == ActionKind.Remount) { Assert.False(Dispatcher.HasOpenResources); Mount(); return new(Outcome.Success); }
        (uint Result, uint Error) answer;
        byte[]? data = null;
        if (op.Kind == ActionKind.Delete) answer = Packet(16, 0, Bstr(op.Name));
        else if (op.Kind == ActionKind.Rename) answer = Packet(17, 0, Bstr(op.Name), 0, Bstr(op.Target, TargetAddress));
        else if(op.Kind==ActionKind.HardLink)
        {
            var locked=Packet(8,0,Bstr(op.Name),0xfffffffe); if(locked.Error!=0) return Convert(locked.Error);
            answer=Packet(1021,0,Bstr(op.Target),locked.Result,0);
            Assert.Equal((uint.MaxValue,0u),Packet(15,locked.Result));
        }
        else
        {
            // Input admission checks existence; UPDATE would create a missing file.
            answer = Packet(op.Kind == ActionKind.Create ? 1006u : 1005u, FileHandle >> 2, 0, Bstr(op.Name));
            if (answer.Error != 0) return Convert(answer.Error);
            uint handle = Ram.Read32(FileHandle + 36);
            if (op.Kind is ActionKind.Write or ActionKind.Resize)
            {
                Assert.Equal((uint.MaxValue, 0u), Packet(1007, handle));
                Assert.Equal((uint.MaxValue, 0u), Packet(1004, FileHandle >> 2, 0, Bstr(op.Name))); handle = Ram.Read32(FileHandle + 36);
            }
            if (op.Kind is ActionKind.Write or ActionKind.Read)
            {
                answer = Packet(1008, handle, (uint)op.Offset, uint.MaxValue);
                Assert.Equal(0u, answer.Error);
                if (op.Kind == ActionKind.Write) op.Data!.CopyTo(Ram.Bytes, (int)DataAddress);
                answer = Packet(op.Kind == ActionKind.Write ? 87u : 82u, handle, DataAddress, (uint)op.Data!.Length);
                if (answer.Error == 0)
                {
                    if (op.Kind == ActionKind.Read) data = Ram.Bytes.AsSpan((int)DataAddress, (int)answer.Result).ToArray();
                    else Assert.Equal((uint)op.Data.Length, answer.Result);
                }
            }
            else if (op.Kind == ActionKind.Resize) answer = Packet(1022, handle, (uint)op.Offset, uint.MaxValue);
            Assert.Equal((uint.MaxValue, 0u), Packet(1007, handle));
        }
        return answer.Error == 0 ? new(Outcome.Success, data) : Convert(answer.Error);
    }
    private static Answer Convert(uint error) => error switch { 205 => new(Outcome.Missing), 203 => new(Outcome.Exists), _ => throw new InvalidDataException($"DOS error {error}") };
}
