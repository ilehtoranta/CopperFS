using CopperFS.Prototype;
using System.Text;

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project prototypes/CopperFS.Prototype.Demo -c Release");
    return 2;
}
Console.WriteLine("CPX3 disposable host demonstration; no native timing or physical-media claim.");
Console.WriteLine("AU    Page  Scratch  Inline  Files  Peak journal images  Free units");
foreach (int allocation in new[] { 512, 1024, 2048, 4096 })
{
    var device = new DemoDevice(); int page = Math.Max(1024, allocation);
    byte[] scratch = new byte[PrototypeVolume<DemoDevice>.RequiredScratchBytes(page)];
    PrototypeVolume<DemoDevice>.Format(device, allocation, scratch, 0x4350583300000001);
    var volume = new PrototypeVolume<DemoDevice>(device, scratch);
    for (int i = 0; i < 32; i++) Require(volume.Put(Encoding.ASCII.GetBytes($"tiny{i:D2}"), new byte[127]));
    byte[] data = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i * 37)).ToArray();
    Require(volume.Put("large"u8, data)); volume.TryGetInfo("large"u8, out var info);
    byte[] actual = new byte[data.Length]; volume.Read(info.Id, 0, actual);
    if (!actual.AsSpan().SequenceEqual(data)) throw new InvalidDataException("Readback mismatch.");
    Require(volume.Create("unwritten"u8, out ulong id)); Require(volume.Preallocate(id, 0, 16, out _));
    Require(volume.Write(id, (ulong)allocation + 7, "initialized middle"u8, out _));
    Require(volume.Truncate(id, 13)); Require(volume.Truncate(id, 10000));
    var report = volume.Check(); int peak = volume.MaximumTransactionPages;
    var reopened = new PrototypeVolume<DemoDevice>(device, scratch);
    if (reopened.Check() != report) throw new InvalidDataException("Remount mismatch.");
    Console.WriteLine($"{allocation,4}  {page,4}  {scratch.Length,7}  {volume.InlineCapacity,6}  {report.Files,5}  {peak,19}  {report.FreeUnits,10}");
}
Console.WriteLine("Journal payloads stream through six pages; scratch also includes 12 KiB of fixed workspace.");
Console.WriteLine("Imports and large writes use multiple transactions. CPX1/CPX2 images require reformatting.");
return 0;

static void Require(MutationResult result)
{ if (result != MutationResult.Success) throw new InvalidOperationException($"Unexpected result: {result}"); }

sealed class DemoDevice : IBlockDevice
{
    private readonly byte[] bytes = new byte[4 * 1024 * 1024];
    public int SectorSize => 512;
    public ulong SectorCount => (ulong)bytes.Length / 512;
    public void Read(ulong sector, Span<byte> destination) => bytes.AsSpan(checked((int)(sector * 512)), destination.Length).CopyTo(destination);
    public void Write(ulong sector, ReadOnlySpan<byte> source) => source.CopyTo(bytes.AsSpan(checked((int)(sector * 512)), source.Length));
    public void Flush() { }
}
