using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using CopperSFS.Sfs2Reference;

if (args.Length != 3 && !(args.Length == 4 && args[3] == "capture-redo") && !(args.Length == 5 && args[3] == "verify"))
    throw new ArgumentException("Usage: CopperSFS.Sfs2Reference <original-handler> <licensed-kickstart31> <new-output-directory> [capture-redo | verify <input-image>]");
var verify = args.Length == 5;
var capture = args.Length == 4 ? new NativeRedoCapture() : null;
var handler = Path.GetFullPath(args[0]); var rom = Path.GetFullPath(args[1]); var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("The output directory must be new.");
Directory.CreateDirectory(output);
var stage = Path.Combine(output, "boot"); Directory.CreateDirectory(Path.Combine(stage, "S"));
File.Copy(handler, Path.Combine(stage, "SFSHANDLER"));
var compilation = AmigaM68kCompiler.Compile(new M68kCompilationRequest
{
    AssemblyPath = typeof(ReferenceProbe).Assembly.Location,
    EntryPoint = verify ? "CopperSFS.Sfs2Reference.ReferenceProbe::RunVerify" : "CopperSFS.Sfs2Reference.ReferenceProbe::Run",
    Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Hunk,
    RuntimeProfile = M68kRuntimeProfile.Application, ExceptionMode = M68kExceptionMode.Full,
    IncludedExportNames = []
});
if (!compilation.FrameworkAnalysis.IsCompatible) throw new InvalidOperationException("Probe compilation is incompatible.");
File.WriteAllBytes(Path.Combine(stage, "PROBE"), compilation.Image);
File.WriteAllText(Path.Combine(stage, "S", "startup-sequence"), "PROBE\n", Encoding.ASCII);
var boot = await FileSystemAdf.CreateAsync(stage, "Sfs2FormatReference");
var image = Path.Combine(output, "reference.hdf");
if (verify) File.Copy(Path.GetFullPath(args[4]), image);
else using (var stream = File.Create(image)) stream.SetLength(16 * 1024 * 1024);
byte status = 255;
bool started = false;
long frames = 0, cycles = 0;
uint pc = 0;
var elapsed = System.Diagnostics.Stopwatch.StartNew();
using (var machine = new LightweightA500Machine(new LightweightA500Configuration
{
    CpuModel = M68kCpuModel.M68020, ChipRamBytes = 512 * 1024, FastRamBytes = 2 * 1024 * 1024,
    FloppyDriveCount = 1,
    Hardfiles = [new AmigaHardfileConfiguration(0, image, mountMode: AmigaHardfileMountMode.Partition,
        partition: new AmigaHardfilePartitionMetadata
        {
            DeviceName = "DH0", SizeBlockLongs = 128, Surfaces = 2, SectorsPerBlock = 1,
            BlocksPerTrack = 32, LowCylinder = 0, HighCylinder = 511, ReservedBlocks = 2,
            NumBuffers = 32, DosType = 0x53465302, BootPriority = -127,
            MaxTransfer = 0x1FE00, Mask = 0x7FFFFFFF
        }) { ExposeBootNode = false, IoHook = capture }]
}))
{
    machine.LoadKickstart(await File.ReadAllBytesAsync(rom)); machine.MountAdf(0, boot);
    machine.SetDriveWriteProtected(0, false); machine.Reset();
    for (var frame = 0; frame < 30000 && elapsed.Elapsed < TimeSpan.FromMinutes(5); frame++)
    {
        try { machine.ExecuteFrame(); }
        catch (NativeRedoCut) { status = 99; break; }
        status = machine.ParallelOutputPins;
        if (status == 101) started = true;
        if (started && status is > 0 and < 100) break;
    }
    if (capture?.Captured != true) machine.FlushPersistentStorage();
    frames = machine.CompletedFrames; cycles = machine.Cpu.Cycles; pc = machine.Cpu.ProgramCounter;
}
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
    {
        status, passed = status == 42 || capture?.Captured == true, mode = verify ? "verify" : capture is null ? "format" : "capture-redo",
        nativeRedoCaptured = capture?.Captured, image, imageSha256 = Hash(image),
        originalHandlerSha256 = Hash(handler), kickstartSha256 = Hash(rom),
        compilerSha256 = Hash(typeof(M68kCompilationRequest).Assembly.Location),
        emulatorSha256 = Hash(typeof(LightweightA500Machine).Assembly.Location),
        frames, cycles, pc = $"{pc:X8}",
        elapsedSeconds = elapsed.Elapsed.TotalSeconds
    }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Reference format/create result: {status}; {output}");
return status == 42 || capture?.Captured == true ? 0 : 1;

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}
