using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CrashModelTests
{
    [Fact] public void OlderAndNewerVersionsOfTheSameSectorRemainCrashCandidates()
    {
        foreach (int selected in new[] { 0, 1 })
        {
            var disk = new CrashDevice(new byte[1024]);
            disk.Write(0, Enumerable.Repeat((byte)0x11, 512).ToArray());
            disk.Write(0, Enumerable.Repeat((byte)0x22, 512).ToArray());
            Assert.Equal(2, disk.PendingVersions);
            disk.PersistenceOrder = [selected]; disk.Cut = disk.Events.Count + 1;
            Assert.Throws<PowerLoss>(disk.Flush);
            Assert.All(disk.Durable[..512], b => Assert.Equal(selected == 0 ? 0x11 : 0x22, b));
        }
    }

    [Fact] public void SuccessfulBarrierRetiresAllVersionsAndKeepsTheLastSubmission()
    {
        var disk = new CrashDevice(new byte[1024]);
        disk.Write(0, Enumerable.Repeat((byte)1, 512).ToArray());
        disk.Write(0, Enumerable.Repeat((byte)2, 512).ToArray()); disk.Flush();
        Assert.Equal(0, disk.PendingVersions); Assert.All(disk.Durable[..512], b => Assert.Equal(2, b));
        disk.Cut = disk.Events.Count + 1; disk.Persistence = 1;
        Assert.Throws<PowerLoss>(() => disk.Write(0, new byte[512]));
        Assert.All(disk.Durable[..512], b => Assert.Equal(2, b)); // before submission
    }

    [Fact] public void ExhaustiveSmallPersistenceOrdersIncludeBothOrdersAndEverySubset()
    {
        var schedules = PersistenceSchedules.Enumerate(3).ToArray();
        Assert.Equal(16, schedules.Length); // 1 + 3 + 6 + 6 ordered subsets
        Assert.Equal(16, schedules.Select(x => string.Join(',', x)).Distinct().Count());
        foreach (int[] order in schedules)
        {
            var disk = new CrashDevice(new byte[1024]);
            for (byte value = 1; value <= 3; value++) disk.Write(0, Enumerable.Repeat(value, 512).ToArray());
            disk.PersistenceOrder = order; disk.Cut = disk.Events.Count + 1;
            Assert.Throws<PowerLoss>(disk.Flush);
            Assert.Equal(order.Length == 0 ? 0 : order[^1] + 1, disk.Durable[0]);
        }
    }
}

internal static class PersistenceSchedules
{
    internal static IEnumerable<int[]> Enumerate(int count)
    {
        if (count is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(count));
        return Visit([]);
        IEnumerable<int[]> Visit(int[] prefix)
        {
            yield return prefix;
            for (int i = 0; i < count; i++)
                if (!prefix.Contains(i)) foreach (var child in Visit([..prefix, i])) yield return child;
        }
    }
}
