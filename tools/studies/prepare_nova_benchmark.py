"""Derive the isolated runner from the repository's A86 benchmark harness.

Guest programs are linked unchanged. Frozen guest HUNK inputs are mandatory.
No third-party source or media is copied into tracked files.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
source = (root / 'tools/CopperCPX.Bench/Program.cs').read_text(encoding='utf-8-sig')
source = '// Derived from tools/CopperCPX.Bench/Program.cs at 6a4380d.\n' + source
source = source.replace('using CopperFS.Cpx4.Reference;', 'using CopperNova.Reference;')
start = source.index('        var cpx = new MemoryDevice(')
end = source.index('        var ffs = CreateEmptyFfs();', start)
source = source[:start] + '''        byte[] nova = NovaImage.Format(DiskBytes);
        File.WriteAllBytes(Path.Combine(ArtifactRoot, "nova-empty-16m.hdf"), nova);
''' + source[end:]
start = source.index('        var compiled = AmigaM68kCompiler.Compile(')
end = source.index('        File.WriteAllBytes(Path.Combine(ArtifactRoot, "BENCHPROBE"), probe);', start)
source = source[:start] + '''        // Require the exact previously qualified guest program, without recompiling it.
        probe = File.ReadAllBytes(Input("NOVA_BENCH_PROBE"));
''' + source[end:]
source = source.replace('CpxHandler, cpx.Bytes', 'CpxHandler, nova')
source = source.replace('Hash(cpx.Bytes)', 'Hash(nova)')
start = source.index('                var checkDevice = new MemoryDevice(finishedMedia);')
end = source.index('\n            }\n            else if', start)
source = source[:start] + '''                var check = NovaImage.Check(finishedMedia);
                var file = check.Entries.Single(entry => entry.Path == "/Bench/Renamed.bin");
                var temp = check.Entries.Single(entry => entry.Path == "/Bench/Temp.bin");
                if(file.Size != PayloadBytes || file.Sha256 != Hash(ExpectedPayload()))
                    throw new InvalidDataException("Prototype persisted payload differs from the expected data.");
                if(temp.Size != 1024 || temp.Sha256 != Hash(Enumerable.Repeat((byte)0x5A,1024).ToArray()))
                    throw new InvalidDataException("Prototype temporary file differs from expected data.");
                result = result with { IndependentCheck = JsonSerializer.Serialize(check) };
''' + source[end:]
start = source.index('    private sealed class MemoryDevice(')
end = source.index('    private sealed class IoHook', start)
source = source[:start] + source[end:]
source = source.replace('CPX4', 'CopperNova prototype').replace('Cpx4', 'Prototype')
source = source.replace('"native-cpx4-handler","CopperCopperNova prototype"', '"nova-prototype-handler","CopperNovaPrototype"')
source = source.replace('Usage: CopperCPX.Bench', 'Usage: CopperNova.Bench')
source = source.replace('tools/CopperCPX.Bench/README.md.', 'tools/CopperNova.Bench/README.md.')
source = source.replace('"cpx4-crossfs-benchmark"', '"nova-crossfs-benchmark"')
(root / 'tools/CopperNova.Bench/Program.cs').write_text(source, encoding='utf-8')
project = (root / 'tools/CopperCPX.Bench/CopperCPX.Bench.csproj').read_text(encoding='utf-8-sig')
project = project.replace('CopperFS.Cpx4.Reference/CopperFS.Cpx4.Reference', 'CopperNova.Reference/CopperNova.Reference')
project = project.replace('  </ItemGroup>', '''    <Compile Include="../CopperCPX.Bench/MixedProbe.cs" Link="MixedProbe.cs" />
    <Compile Include="../CopperCPX.Bench/StreamingProbe.cs" Link="StreamingProbe.cs" />
  </ItemGroup>''')
(root / 'tools/CopperNova.Bench/CopperNova.Bench.csproj').write_text(project, encoding='utf-8')
