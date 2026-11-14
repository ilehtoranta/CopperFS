using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class PacketContractTests
{
    public static IEnumerable<object[]> Supported() => PacketContracts.All.Where(x => x.Status == PacketStatus.ImplementedSubset).Select(x => new object[] { x.Action, x.Name });
    public static IEnumerable<object[]> Refused() => PacketContracts.All.Where(x => x.Status == PacketStatus.UnsupportedByReference).Select(x => new object[] { x.Action });
    [Fact] public void InventoryHasUniquePacketNumbersAndNames()
    {
        Assert.Equal(PacketContracts.All.Length, PacketContracts.All.Select(x => x.Action).Distinct().Count());
        Assert.All(PacketContracts.All, x => Assert.False(string.IsNullOrWhiteSpace(x.Coverage)));
        Assert.DoesNotContain(PacketContracts.All, x => x.Status is PacketStatus.Pending or PacketStatus.FormatBlocked);
        Assert.Equal(new uint[] { 20, 30, 33 }, PacketContracts.All.Where(x => x.Status == PacketStatus.UnsupportedByReference).Select(x => x.Action));
    }
    [Theory, MemberData(nameof(Refused))]
    public void UnsupportedPacketsFinishInOneStepAndRespectMediaState(uint action)
    {
        using var d = new DispatcherDriver(512); byte[] before = (byte[])d.Image.Clone();
        void Check(uint error)
        {
            uint p = DispatcherDriver.PacketAddress;
            d.Ram.Write32(p + 8, action);
            // Stale results and unusable arguments must be ignored and promptly replied.
            for (uint offset = 12; offset <= 36; offset += 4) d.Ram.Write32(p + offset, uint.MaxValue);
            Assert.Equal(CpxError.None, d.Dispatcher.BeginPacket(p));
            Assert.Equal(StepResult.Complete, d.Dispatcher.Step());
            Assert.Equal(0u, d.Ram.Read32(p + 12)); Assert.Equal(error, d.Ram.Read32(p + 16));
            Assert.False(d.Dispatcher.Busy); Assert.False(d.Dispatcher.PacketDeferred);
            Assert.False(d.Dispatcher.HasOpenResources); Assert.False(d.Dispatcher.HasRecordWaits);
        }
        Check(209);
        Assert.Equal((uint.MaxValue, 0u), d.Packet(31, 1)); Check(226);
        Assert.Equal((uint.MaxValue, 0u), d.Packet(31, 0)); Check(209);
        d.Dispatcher.MediaRemoved(); Check(226); Assert.False(d.Dispatcher.MediaAvailable);
        Assert.Equal(before, d.Image);
    }
    [Theory, MemberData(nameof(Supported))]
    public void ImplementedPacketHasAConcreteSuccessContract(uint action, string name)
    {
        using var d = new DispatcherDriver(512);
        Assert.Equal(Outcome.Success, d.Execute(new(ActionKind.Create, "file")).Outcome);
        Assert.Equal(Outcome.Success, d.Execute(new(ActionKind.Write, "file", Data: [1, 2, 3])).Outcome);
        uint fh = DispatcherDriver.FileHandle, handle = 0, heldLock = 0;
        if (action is 82 or 87 or 1007 or 1008 or 1022 or 1028 or 1030 or 1031 or 1034 or 2008 or 2009 or 26400 or 26401 or 26402 or 26403 or 26410)
        {
            Assert.Equal((uint.MaxValue, 0u), d.Packet(1004, fh >> 2, 0, d.Bstr("file"))); handle = d.Ram.Read32(fh + 36);
        }
        if (action is 15 or 23 or 1021 or 1026 or 26408) { heldLock = d.Packet(8, 0, d.Bstr("file"), 0xfffffffe).Result; Assert.NotEqual(0u, heldLock); }
        if (action==0x43535452) { d.Ram.Write32(89000,32); d.Ram.Write32(89004,1); d.Ram.Write32(89008,2); }
        if (action is 1024 or 26406)
        {
            "file\0"u8.CopyTo(d.Ram.Bytes.AsSpan(89000));
            Assert.Equal((uint.MaxValue,0u),d.Packet(1021,0,d.Bstr("soft"),89000,1));
            "soft\0"u8.CopyTo(d.Ram.Bytes.AsSpan(89000));
        }
        if (action is 24 or 26409) Assert.Equal((uint.MaxValue, 0u), d.Packet(23, 0, 86000 >> 2));
        if (action is 1033 or 1035)
        {
            d.Execute(new(ActionKind.Create,"second"));
            if(action==1035) Assert.Equal((uint.MaxValue,0u),ExAllPacketTests.All(d,16,1,88000));
        }
        d.Ram.Write32(84000, 1); d.Ram.Write32(84004, 2); d.Ram.Write32(84008, 3);
        d.Ram.Write32(85000, 0); d.Ram.Write32(85004, 2);
        d.Ram.Write32(85008, 0); d.Ram.Write32(85012, 1);
        if (action == 2009) Assert.Equal((uint.MaxValue, 0u), d.Packet(2008, handle, 2, 1, 1));
        if (action == 26403) Assert.Equal((uint.MaxValue, 0u), d.Packet(26402, handle, 85000, 85008, 1));
        uint notify = 0;
        if (action is 4097 or 4098)
        {
            notify = NotificationLifecycleTests.Request(d, "file");
            if (action == 4098) Assert.Equal((uint.MaxValue, 0u), d.Packet(4097, notify));
        }
        (uint Result, uint Error) result = action switch
        {
            7 or 18 or 27 or 32 or 1027 => d.Packet(action),
            8 => d.Packet(8, 0, d.Bstr("file"), 0xfffffffe),
            15 => d.Packet(15, heldLock),
            16 => d.Packet(16, 0, d.Bstr("file")),
            17 => d.Packet(17, 0, d.Bstr("file"), 0, d.Bstr("renamed", DispatcherDriver.TargetAddress)),
            19 => d.Packet(19),
            21 => d.Packet(21, 0, 0, d.Bstr("file"), 4),
            22 => d.Packet(22, 0, d.Bstr("dir")),
            23 or 26408 => d.Packet(action, heldLock, 86000 >> 2),
            24 or 26409 => d.Packet(action, 0, 86000 >> 2),
            25 => d.Packet(25, 86000 >> 2),
            26 => d.Packet(26, 0, 86000 >> 2),
            28 => d.Packet(28, 0, 0, d.Bstr("file"), d.Bstr("comment", DispatcherDriver.TargetAddress)),
            29 => d.Packet(29),
            31 => d.Packet(31,1),
            34 => d.Packet(34, 0, 0, d.Bstr("file"), 84000),
            40 => d.Packet(40),
            82 => d.Packet(82, handle, 87000, 3),
            87 => d.Packet(87, handle, 84000, 3),
            1004 or 1005 or 1006 => d.Packet(action, fh >> 2, 0, d.Bstr("file")),
            1007 => d.Packet(1007, handle),
            1008 => d.Packet(1008, handle, 2, uint.MaxValue),
            1022 => d.Packet(1022, handle, 2, uint.MaxValue),
            1021 => d.Packet(1021,0,d.Bstr("linked"),heldLock,0),
            1024 or 26406 => d.Packet(action,0,89000,90000,100),
            1023 => d.Packet(1023, 1),
            1026 => d.Packet(1026, fh>>2, heldLock),
            1028 => d.Packet(1028, 1, fh>>2, uint.MaxValue),
            1030 or 1031 => d.Packet(action, handle),
            1033 => ExAllPacketTests.All(d,16,1,88000),
            1035 => ExAllPacketTests.End(d,88000),
            1034 or 26410 => d.Packet(action, handle, 86000 >> 2),
            0x43535452 => d.Packet(action,89000),
            26407 => d.Packet(26407, 6, 86000, 8),
            26400 or 26401 => d.Packet(action, handle, 85000, uint.MaxValue, 85100),
            2008 or 2009 => d.Packet(action, handle, 2, 1, 1),
            26402 or 26403 => d.Packet(action, handle, 85000, 85008, 1),
            4097 or 4098 => d.Packet(action, notify),
            1036 => d.Packet(1036,0,0,d.Bstr("file"),0x1234abcd),
            9 => d.Packet(9,d.Bstr("Work")),
            1020 => d.Packet(1020,d.Bstr("Work"),CpxArena.Magic),
            _ => throw new InvalidDataException($"Missing executable contract: {name}")
        };
        Assert.Equal(0u, result.Error);
        uint expected = action switch { 7 => 0x5678 >> 2, 32 => CpxArena.Magic, 18 or 29 or 1008 => 0, 82 or 87 => 3, 1022 => 2, 1024=>4, 26406=>15, _ => uint.MaxValue };
        if (action is 8 or 19 or 22 or 1030 or 1031) { Assert.NotEqual(0u, result.Result); Assert.Equal((uint.MaxValue, 0u), d.Packet(15, result.Result)); }
        else Assert.Equal(expected, result.Result);
        if (action == 82) Assert.Equal(new byte[] { 1, 2, 3 }, d.Ram.Bytes[87000..87003]);
        if (action is 26400 or 26401) { Assert.Equal(0u, d.Ram.Read32(85100)); Assert.Equal(action == 26400 ? 0u : 2u, d.Ram.Read32(85104)); }
        if (action is 1004 or 1005 or 1006 or 1026) handle = d.Ram.Read32(fh + 36);
        if (handle != 0 && action != 1007) Assert.Equal((uint.MaxValue, 0u), d.Packet(1007, handle));
        if (heldLock != 0 && action is not (15 or 1026)) Assert.Equal((uint.MaxValue, 0u), d.Packet(15, heldLock));
        if (action is 24 or 26409) Assert.Equal((0u,232u),d.Packet(action,0,86000>>2));
        if (action == 4097) Assert.Equal((uint.MaxValue, 0u), d.Packet(4098, notify));
        if (action == 31) Assert.Equal((uint.MaxValue,0u),d.Packet(31));
        if (action == 1033) Assert.Equal((uint.MaxValue,0u),ExAllPacketTests.End(d,88000));
        Assert.False(d.Dispatcher.HasOpenResources); new IndependentImage(d.Image);
    }

    [Theory][InlineData(82, uint.MaxValue)][InlineData(87, uint.MaxValue)][InlineData(1007, 0u)]
    [InlineData(1008, uint.MaxValue)][InlineData(1022, uint.MaxValue)][InlineData(26400, 0u)][InlineData(26401, 0u)]
    public void InvalidHandleCannotChangeMediaOrAllocateAResource(uint action, uint failure)
    {
        using var d = new DispatcherDriver(512); byte[] before = (byte[])d.Image.Clone();
        Assert.Equal((failure, 211u), d.Packet(action, 0x1234)); Assert.False(d.Dispatcher.HasOpenResources); Assert.Equal(before, d.Image);
    }
}
