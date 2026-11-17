using CopperFS.Prototype;
using static CopperFS.Prototype.Tests.DesignTests;

namespace CopperFS.Prototype.Tests;

public class MappingRecoveryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory] [InlineData("unwritten")] [InlineData("shrink")] [InlineData("replace")] [InlineData("grow")] [InlineData("preallocate")]
    public void MappingAndReplacementSurviveSectorAndBarrierCuts(string operation)
    {
        var initial = Fresh(); var v = new PrototypeVolume<MemoryDevice>(initial, Workspace());
        v.Create("file"u8, out ulong id);
        if (operation == "unwritten")
        {
            v.Trim(); v.Preallocate(id, 0, 12, out _);
            for (uint i = 1; i <= 5; i++) v.Preallocate(id, i * 100 * 512, 1, out _);
        }
        else { v.Append(id, 0, Pattern(10000), out _); if (operation is "grow" or "preallocate") v.Truncate(id, 103); }
        v.Create(1, "sub"u8, true, out ulong sub); v.Create(sub, "victim"u8, false, out ulong victim); v.Append(victim, 0, "victim data"u8, out _);
        var trace = new CrashDevice(initial.Bytes); Mutate(trace);
        output.WriteLine($"{trace.Events.Count} {operation} cuts x 5 persistence schedules = {trace.Events.Count * 5} crash recoveries.");
        for (int cut = 1; cut <= trace.Events.Count; cut++) for (int mode = 0; mode < 5; mode++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = mode };
            Assert.Throws<PowerLoss>(() => Mutate(fault)); var finalDevice = new MemoryDevice(fault.Durable);
            var final = new PrototypeVolume<MemoryDevice>(finalDevice, Workspace()); final.Check(); var oracle = new IndependentImage(finalDevice.Bytes);
            byte[] payload = oracle.Read(id);
            if (operation == "unwritten")
            {
                byte[] middle = payload.AsSpan(519, 1301).ToArray(); Assert.True(middle.All(b => b == 0) || middle.AsSpan().SequenceEqual(Pattern(1301)));
                Assert.All(payload[..519], b => Assert.Equal(0, b)); Assert.All(payload[1820..], b => Assert.Equal(0, b));
            }
            else if (operation == "shrink") Assert.True(payload.AsSpan().SequenceEqual(Pattern(103)) || payload.AsSpan().SequenceEqual(Pattern(10000)));
            else if (operation == "grow")
            {
                Assert.True(payload.Length is 103 or 10001); Assert.Equal(Pattern(103), payload[..103]);
                if (payload.Length > 103) { Assert.All(payload[103..10000], b => Assert.Equal(0, b)); Assert.Equal(7, payload[10000]); }
            }
            else if (operation == "preallocate")
            { Assert.True(payload.Length is 103 or 10752); Assert.Equal(Pattern(103), payload[..103]); Assert.All(payload[103..], b => Assert.Equal(0, b)); }
            else
            {
                Assert.Equal(Pattern(10000), payload);
                bool old = oracle.Names.ContainsKey((1, "file")); Assert.Equal(old ? victim : id, oracle.Names[(sub, "victim")]);
            }
        }
        void Mutate(CrashDevice device)
        {
            var volume = new PrototypeVolume<CrashDevice>(device, Workspace());
            MutationResult result = operation == "unwritten" ? volume.Write(id, 519, Pattern(1301), out _) : operation == "shrink" ? volume.Truncate(id, 103) : operation == "grow" ? volume.Append(id, 10000, new byte[] { 7 }, out _) : operation == "preallocate" ? volume.Preallocate(id, 10240, 1, out _) : volume.Rename(1, "file"u8, sub, "victim"u8, true);
            Assert.Equal(MutationResult.Success, result);
        }
    }
}
