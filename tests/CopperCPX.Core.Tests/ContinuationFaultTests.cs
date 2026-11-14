using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class ContinuationFaultTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static void Start(ref CpxEngine<Ram> e, Ram ram, int operation)
    {
        "file"u8.CopyTo(ram.Bytes.AsSpan(90000)); "renamed"u8.CopyTo(ram.Bytes.AsSpan(91000));
        ram.Bytes.AsSpan(92000, 700).Fill(0x7e);
        Assert.Equal(CpxError.None, operation switch
        {
            0 => e.BeginCreate(Wide.From(1), 91000, 7),
            1 => e.BeginRename(Wide.From(1), 90000, 4, Wide.From(1), 91000, 7),
            2 => e.BeginDelete(Wide.From(1), 90000, 4),
            3 => e.BeginResize(Wide.From(2), Wide.From(123)),
            _ => e.BeginMappedWrite(Wide.From(2), Wide.From(1024), 92000, 700)
        });
    }
    private static void Service(CpxIo io, Ram ram, MemoryDevice disk)
    {
        ulong sector = (ulong)io.Sector.High << 32 | io.Sector.Low;
        if (io.Kind == IoKind.Read) disk.Read(sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
        else if (io.Kind == IoKind.Write) disk.Write(sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
        else { Assert.Equal(IoKind.Flush, io.Kind); disk.Flush(); }
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public void EveryOperationIoCanFailOrCancelWithoutReusingLiveBuffers(int operation)
    {
        var original = EngineTests.Fresh(512); var reference = new Cpx4Volume<Disk>(original, new byte[36864]);
        reference.Create("file"u8, out _); reference.Write(2, 0, Enumerable.Repeat((byte)0x51, 1024).ToArray(), out _);
        int requests = 0;
        {
            var ram = new Ram(new byte[100000]); var disk = new MemoryDevice(original.Bytes); var e = FileMutationTests.Mount(disk, ram);
            Start(ref e, ram, operation);
            for (int steps = 0; steps < 20000; steps++)
            {
                var step = e.Step(); if (step == StepResult.Complete) break; Assert.NotEqual(StepResult.Failed, step);
                if (step != StepResult.NeedIo) continue;
                var io = e.Request; Service(io, ram, disk); e.CompleteIo(io.Token, io.Bytes, true); requests++;
            }
            Assert.Equal(StepResult.Complete, e.Step());
        }
        for (int at = 1; at <= requests; at++) for (int fault = 0; fault < 3; fault++)
        {
            var ram = new Ram(new byte[100000]); var disk = new MemoryDevice(original.Bytes); var e = FileMutationTests.Mount(disk, ram);
            Start(ref e, ram, operation); int index = 0; CpxIo failed = default; bool injected = false;
            for (int steps = 0; steps < 20000; steps++)
            {
                e = new(ram, 32); var step = e.Step();
                if (step == StepResult.Failed) break; Assert.NotEqual(StepResult.Complete, step);
                if (step != StepResult.NeedIo) continue;
                var io = e.Request;
                if (++index != at) { Service(io, ram, disk); e.CompleteIo(io.Token, io.Bytes, true); continue; }
                failed = io; injected = true;
                Assert.Equal(CpxError.StaleCompletion, e.CompleteIo(io.Token + 1, io.Bytes, true));
                Assert.True(e.HasOutstandingIo); Assert.Equal(CpxError.Busy, e.BeginOperation(CpxOperation.Flush));
                if (fault == 2)
                {
                    e.Cancel(); Assert.True(e.HasOutstandingIo); Assert.Equal(StepResult.NeedIo, e.Step());
                    Assert.Equal(CpxError.Busy, e.Initialize(512, 512, Wide.From(8192), 2));
                }
                // Uncertain writes may reach media even when completion fails.
                // Short completion applies half a request; flush errors are failures.
                if (fault == 1 && io.Kind == IoKind.Write)
                    disk.Write(io.Sector.Low, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes / 2));
                else Service(io, ram, disk);
                bool success = fault == 2 || fault == 1 && io.Kind is IoKind.Read or IoKind.Write;
                uint actual = fault == 1 ? io.Bytes / 2 : io.Bytes;
                Assert.Equal(fault == 2 ? CpxError.Cancelled : CpxError.Io, e.CompleteIo(io.Token, actual, success));
            }
            Assert.True(injected); Assert.Equal(StepResult.Failed, e.Step()); Assert.False(e.HasOutstandingIo); Assert.False(e.IsMounted);
            Assert.Equal(CpxError.None, e.Initialize(512, 512, Wide.From(8192), 2)); e.BeginOperation(CpxOperation.Mount);
            Assert.Equal(StepResult.NeedIo, e.Step());
            Assert.Equal(CpxError.StaleCompletion, e.CompleteIo(failed.Token, failed.Bytes, true));
            EngineTests.Drive(ref e, ram, disk); new IndependentImage(disk.Bytes); new Cpx4Volume<MemoryDevice>(disk, new byte[36864]).Check();
        }
        output.WriteLine($"Operation {operation}: {requests} I/O boundaries × three faults; replay, ownership and stale epoch completion checked.");
    }
}
