using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

// Matrix construction and execution live in C#. PowerShell is an argument shim.
internal static class MatrixCommand
{
    internal static async Task<string[]> Prepare(string[] arguments)
    {
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string[] names=["RepositoryRoot","NativeArtifactRoot","ArtifactRoot","OriginalFfsHandler","OriginalSfs2Handler",
            "A1200Rom","LegacyRom","Backend","MachineProfile","DriveProfile","PayloadCachePolicy","TimingProfiles",
            "TestAssembly","Smoke","Workers","ReferenceProvenance","A1200Kickstart","A1200Rom31","FileSystemProfile"];
        for(var i=0;i<arguments.Length;i++)
        {
            var key=arguments[i];
            if(!key.StartsWith("--") || !names.Contains(key[2..],StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown matrix option: {key}");
            key=key[2..];
            var value=key.Equals("Smoke",StringComparison.OrdinalIgnoreCase) ? "true" :
                ++i<arguments.Length ? arguments[i] : throw new ArgumentException($"Missing value: {key}");
            if(!values.TryAdd(key,value)) throw new ArgumentException($"Duplicate matrix option: {key}");
        }
        string Option(string key,string fallback="")=>values.GetValueOrDefault(key,fallback);
        string Required(string key)=>string.IsNullOrWhiteSpace(Option(key)) ? throw new ArgumentException($"Supply --{key}.") : Option(key);
        string Existing(string path)
        {
            var full=Path.GetFullPath(path);
            return File.Exists(full) ? full : throw new FileNotFoundException("Missing benchmark input.",full);
        }
        string Choice(string key,string fallback,params string[] choices)
        {
            var value=Option(key,fallback);
            return choices.FirstOrDefault(c=>c.Equals(value,StringComparison.OrdinalIgnoreCase)) ??
                throw new ArgumentException($"Invalid {key}: {value}; use {string.Join('|',choices)}.");
        }
        var repository=Path.GetFullPath(Required("RepositoryRoot"));
        var destination=Path.GetFullPath(Required("ArtifactRoot"));
        if(Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Use a fresh artifact directory.");
        var backend=Choice("Backend","All","All","ZeroLatency","TimedCopperHdf","A1200Ide");
        var machine=Choice("MachineProfile","All","All","Legacy","A1200Stock","A1200Fast");
        var drive=Choice("DriveProfile","Both","Both","Hdd","Cf");
        var policy=Choice("PayloadCachePolicy","Both","Both","Cached","Bypass");
        var kickstart=Choice("A1200Kickstart","3.0","3.0","3.1","Both");
        var filesystem=Choice("FileSystemProfile","All","All","Ffs","Sfs2","Sfs2CSUN","Sfs2TRST");
        var profiles=filesystem switch
        {
            "Ffs"=>new[]{"ffs"},"Sfs2"=>new[]{"sfs2-csun","sfs2-trst"},
            "Sfs2CSUN"=>new[]{"sfs2-csun"},"Sfs2TRST"=>new[]{"sfs2-trst"},
            _=>StorageMatrixDefinition.DefaultProfiles
        };
        var workers=int.Parse(Option("Workers","4"),CultureInfo.InvariantCulture);
        if(workers is <1 or >16) throw new ArgumentOutOfRangeException("Workers");
        var scenarios=new List<string>();
        if(machine is "All" or "Legacy" && backend is "All" or "ZeroLatency") scenarios.Add("legacy-zero");
        foreach(var version in kickstart=="Both" ? new[]{"3.0","3.1"} : new[]{kickstart})
        foreach(var memory in new[]{"stock","fast"})
        {
            if(machine!="All" && machine!=(memory=="stock" ? "A1200Stock" : "A1200Fast")) continue;
            var prefix=memory+(version=="3.1" ? "-ks31" : "");
            if(backend is "All" or "ZeroLatency") scenarios.Add(prefix+"-zero");
            foreach(var kind in new[]{"hdd","cf"})
            {
                if(drive!="Both" && !drive.Equals(kind,StringComparison.OrdinalIgnoreCase)) continue;
                if(backend is "All" or "TimedCopperHdf") scenarios.Add(prefix+"-"+kind);
                if(backend is "All" or "A1200Ide") scenarios.Add(prefix+"-ide-"+kind);
            }
        }
        if(scenarios.Count==0) throw new ArgumentException("No supported machine/backend combination selected.");
        var variants=policy=="Both" ? new[]{"original","cached","bypass"} : new[]{"original",policy.ToLowerInvariant()};
        var manifest=new JsonObject
        {
            ["NativeManifest"]=Existing(Path.Combine(Required("NativeArtifactRoot"),"benchmark.json")),
            ["OriginalFfs"]=profiles.Contains("ffs") ? Existing(Required("OriginalFfsHandler")) : "",
            ["OriginalSfs2"]=profiles.Any(p=>p.StartsWith("sfs2-",StringComparison.Ordinal)) ? Existing(Required("OriginalSfs2Handler")) : "",
            ["A1200Rom"]=scenarios.Any(s=>!StorageMatrixDefinition.Parse(s).Legacy && !StorageMatrixDefinition.Parse(s).Kickstart31) ? Existing(RequiredRom("A1200Rom","COPPERSCREEN_UPGRADE_A1200_ROM")) : "",
            ["A1200Rom31"]=scenarios.Any(s=>StorageMatrixDefinition.Parse(s).Kickstart31) ? Existing(RequiredRom("A1200Rom31","COPPERSCREEN_UPGRADE_A1200_ROM31")) : "",
            ["LegacyRom"]=scenarios.Contains("legacy-zero") ? Existing(RequiredRom("LegacyRom","COPPERSHARP_KICKSTART31_ROM")) : "",
            ["Scenarios"]=JsonSerializer.SerializeToNode(scenarios),["Variants"]=JsonSerializer.SerializeToNode(variants),
            ["Profiles"]=JsonSerializer.SerializeToNode(profiles),["RequireBufferParity"]=true,
            ["Protocol"]="kickstart-storage-v2",["IdeLayout"]=StorageIdePartition.Layout,["ColdStartEachTrial"]=true,["Trials"]=3,
            ["SourceRoots"]=JsonSerializer.SerializeToNode(new[]{Path.Combine(repository,"tests/CopperFS.Kickstart.Tests"),
                Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("CopperScreenRoot") ?? Path.Combine(repository,"../CopperScreen"),"CopperMod.Amiga.Lightweight")),
                Path.Combine(repository,"tools/CopperFS.StorageBench")})
        };
        string RequiredRom(string key,string environment)
        {
            var value=Option(key,Environment.GetEnvironmentVariable(environment) ?? "");
            return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Supply --{key} or {environment}.") : value;
        }
        if(Option("TimingProfiles") is { Length:>0 } timing)
        {
            var overrides=JsonNode.Parse(File.ReadAllText(Existing(timing)))?.AsObject() ?? throw new JsonException("Expected timing profile object.");
            foreach(var pair in overrides)
            {
                if(pair.Key is not ("Hdd" or "Cf") || pair.Value is not JsonObject)
                    throw new JsonException("Timing overrides must contain Hdd and/or Cf objects.");
                manifest[pair.Key]=pair.Value.DeepClone();
            }
        }
        var provenance=Option("ReferenceProvenance") is { Length:>0 } receipt ? Existing(receipt) : null;
        var assembly=Option("TestAssembly") is { Length:>0 } supplied ? Existing(supplied) : "";
        if(assembly.Length==0)
        {
            var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true };
            foreach(var item in new[]{"build",Path.Combine(repository,"tests/CopperFS.Kickstart.Tests/CopperFS.Kickstart.Tests.csproj"),"-c","Release","--verbosity","quiet"})
                start.ArgumentList.Add(item);
            using var process=Process.Start(start)!;
            await process.WaitForExitAsync();
            if(process.ExitCode!=0) throw new InvalidOperationException("Matrix harness build failed.");
            assembly=Existing(Path.Combine(repository,"tests/CopperFS.Kickstart.Tests/bin/Release/net10.0/CopperFS.Kickstart.Tests.dll"));
        }
        Directory.CreateDirectory(destination);
        if(provenance is not null) File.Copy(provenance,Path.Combine(destination,"reference-provenance.json"));
        var path=Path.Combine(destination,"matrix.json");
        File.WriteAllText(path,manifest.ToJsonString(new JsonSerializerOptions { WriteIndented=true }));
        Environment.SetEnvironmentVariable("COPPERFS_STORAGE_SMOKE",values.ContainsKey("Smoke") ? "1" : null);
        return ["run",path,assembly,workers.ToString(CultureInfo.InvariantCulture)];
    }
}
