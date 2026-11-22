using System.Text.Json;
using CopperNova.Reference;

internal static partial class WorkbenchGate
{
    public static async Task RunResources(string handlerPath, string romPath, string output)
    {
        Directory.CreateDirectory(output);
        var handler = File.ReadAllBytes(handlerPath);
        var rom = File.ReadAllBytes(romPath);
        var media = Path.Combine(output, "resources.rdb");
        var image = CreateImage(handler);
        File.WriteAllBytes(media, image);

        var boot = await FormatBoot(output, "ResourcesEntry", null);
        long length = image.Length - PartitionOffset;
        var (marker, cut, cache) = RunFreshBoot(boot, rom, media, PartitionOffset, length, 2);
        // The seed file commits with two barriers. The final clean Flush issues one.
        if (marker != 210 || cut || cache.Barriers != 3)
            throw new InvalidDataException($"Resource probe failed at marker {marker}, {cache.Barriers} barriers.");

        using var disk = File.OpenRead(media);
        var volume = NovaImage.CheckPacked(disk, PartitionOffset, length);
        if (volume.Generation != 1 || volume.Entries.Count != 1 || volume.Entries[0].Path != "/held" || volume.Entries[0].Size != 4)
            throw new InvalidDataException("Allocation failure changed the committed namespace or file.");
        File.WriteAllText(Path.Combine(output, "qualification.json"), JsonSerializer.Serialize(new
        {
            HandlerSha256 = Hash(handler), RomSha256 = Hash(rom), Kickstart = "2.04", Cpu = "68000",
            ConcurrentHandles = WorkbenchProbe.ResourceProbeCount, ConcurrentLocks = WorkbenchProbe.ResourceProbeCount,
            AllocationFailures = 4, StaleHandleRejectedAfterReopen = true, StaleLockRejectedAfterUnlock = true,
            cache.Writes, cache.Barriers, PhysicalHardware = false
        }));
    }
}
