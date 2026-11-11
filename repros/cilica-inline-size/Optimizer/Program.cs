using Cilica;
using Cilica.AsmIO;

if (args.Length is < 3 or > 4 || args[2] is not ("on" or "off"))
    throw new ArgumentException("Usage: Optimizer <input.dll> <output.dll> <on|off> [max-inlined-method-il-size]");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (input == output) throw new ArgumentException("Input and output must be different files.");
var limit = args.Length == 4 ? int.Parse(args[3]) : 32 * 1024;
var logger = new ConsoleLogger { MinLevel = Environment.GetEnvironmentVariable("CILICA_TRACE") == "1" ? LogLevel.Trace : LogLevel.Warn };
using var resolver = new ModuleResolver(logger);
resolver.AddSearchPaths([Path.GetDirectoryName(input)!]);
resolver.AddTrustedSearchPaths();
var module = resolver.Load(input);
var compilation = new Compilation(module, logger, new CompilationSettings {
    AssumeLittleEndian = false,
    AllowCrossAssemblyIPO = false,
    VerifyEachPass = true,
    EnableInlining = args[2] == "on",
    MaxInlinedMethodILSize = limit
});
var methods = PassManager.GetCandidateMethodsFromIL(module, (_, method) => method.Module == module);
CompilationPipeline.CreateDefault(compilation).Run(methods).ThrowIfFailed();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
module.Save(output, savePdb: false);
Console.WriteLine($"Optimized {methods.Count} methods; inlining={args[2]}; max IL={limit}; output={output}");
