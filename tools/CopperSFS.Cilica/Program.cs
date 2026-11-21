using System.Security.Cryptography;
using System.Text.Json;
using Cilica;
using Cilica.AsmIO;
using CopperSharp.Tools;

if (args.Length != 5 || args[2] is not ("on" or "off") || args[3] is not ("hot" or "cold" or "writer"))
    throw new ArgumentException("Expected input assembly, output directory, inlining mode, method selection and resolver directory.");

var source = Path.GetFullPath(args[0]);
var destination = Path.Combine(Path.GetFullPath(args[1]), "CopperSFS.Core.dll");
if (!Path.GetFileName(source).Equals("CopperSFS.Core.dll", StringComparison.OrdinalIgnoreCase) ||
    source.Equals(destination, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The input must be CopperSFS.Core.dll in a separate directory.");
var inline = args[2] == "on";
bool? selectHot = args[3] switch { "hot" => true, "cold" => false, _ => null };
var inputDirectory = Path.GetFullPath(args[4]);

var logger = new ConsoleLogger { MinLevel = LogLevel.Warn };
using var resolver = new ModuleResolver(logger);
resolver.AddSearchPaths([Path.GetDirectoryName(source)!, inputDirectory]);
resolver.AddTrustedSearchPaths();
var module = resolver.Load(source);
var settings = RestrictedPipeline.CreateSettings(enableInlining: inline);
var compilation = new Compilation(module, logger, settings);
var methods = PassManager.GetCandidateMethodsFromIL(module, (_, method) =>
    method.Module == module && method.DeclaringType.Namespace == "CopperSFS" &&
    method.DeclaringType.Name is ("SfsSharedImagePool" or "AmigaSmartFileSystemWriteCore" or
        "AmigaSmartFileSystemTrstCodec" or "AmigaSmartFileSystemRedoArena") &&
    // The original IL of the CSUN allocator is required by the 68k backend.
    method.Name != "JournalSpace" && (selectHot == null || Hot(method.Name) == selectHot));
var result = RestrictedPipeline.Create(compilation).Run(methods);
result.ThrowIfFailed();
if (!result.Methods.Any()) throw new InvalidOperationException("No SFS core methods matched.");

Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    module.Save(temporary, savePdb: false);
    File.Move(temporary, destination, overwrite: true);
}
finally
{
    File.Delete(temporary);
}
File.WriteAllText(destination + ".cilica.json", JsonSerializer.Serialize(new {
    CilicaRevision = RestrictedPipeline.SourceRevision, CilicaPackageVersion = RestrictedPipeline.PackageVersion,
    settings.EnableInlining, settings.EnableScalarReplacement, settings.VerifyEachPass,
    settings.AssumeLittleEndian, settings.AllowCrossAssemblyIPO,
    InputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
    OutputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))),
    Methods = result.Methods.Select(m => new { Method = m.Method.ToString(), Status = m.Status.ToString() })
}, new JsonSerializerOptions { WriteIndented = true }));

static bool Hot(string method) => method is
    "TryContinueTrstGroup" or "AdmitTrstContinuation" or "TrstContinuationLeaf" or "MaterializeTrstRun" or
    "TrstPoolProofOwned" or "TrstProofImage" or "RelocateOverwrite" or "UpdateTrstBitmapRange" or
    "MarkDataRun" or "WriteOperation" or "TrstGroupHasJournalSpace" or "Finish" or "View" or "Probe" or
    "Edit" or "Read" or "PutSealed" or "Put" or "StoreImage" or "ObjectViewFromRoots" or "FlushTrstGroup";
