using System.Text.Json.Nodes;

public sealed class MatrixCommandTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"copper-storage-options-"+Guid.NewGuid().ToString("N"));
    private readonly string? savedSmoke=Environment.GetEnvironmentVariable("COPPERFS_STORAGE_SMOKE");
    public MatrixCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(root,"native"));
        foreach(var name in new[]{"native/benchmark.json","ffs","sfs","a1200[!].rom","a1200-31[!].rom","legacy.rom","probe.dll"})
            File.WriteAllText(Path.Combine(root,name),"synthetic option-test input");
    }
    private string[] Arguments(string output,params string[] extra)=>
    ["--RepositoryRoot",root,"--NativeArtifactRoot",Path.Combine(root,"native"),"--ArtifactRoot",Path.Combine(root,output),
        "--OriginalFfsHandler",Path.Combine(root,"ffs"),"--OriginalSfs2Handler",Path.Combine(root,"sfs"),
        "--A1200Rom",Path.Combine(root,"a1200[!].rom"),"--LegacyRom",Path.Combine(root,"legacy.rom"),
        "--TestAssembly",Path.Combine(root,"probe.dll"),..extra];
    private static JsonObject Read(string[] prepared)=>JsonNode.Parse(File.ReadAllText(prepared[1]))!.AsObject();

    [Fact]
    public async Task DefaultMatrixContains1188WorkloadsAndBothPolicies()
    {
        var prepared=await MatrixCommand.Prepare(Arguments("default")); var manifest=Read(prepared);
        Assert.Equal("run",prepared[0]); Assert.Equal("4",prepared[3]);
        Assert.Equal(11,manifest["Scenarios"]!.AsArray().Count);
        Assert.Equal(new[]{"original","cached","bypass"},manifest["Variants"]!.AsArray().Select(v=>v!.GetValue<string>()));
        Assert.Equal(1188,manifest["Scenarios"]!.AsArray().Count*3*3*4*manifest["Trials"]!.GetValue<int>());
        Assert.True(manifest["ColdStartEachTrial"]!.GetValue<bool>());
        Assert.True(manifest["RequireBufferParity"]!.GetValue<bool>());
        Assert.Equal(StorageMatrixDefinition.DefaultProfiles,manifest["Profiles"]!.AsArray().Select(v=>v!.GetValue<string>()));
        Assert.EndsWith("a1200[!].rom",manifest["A1200Rom"]!.GetValue<string>());
        Assert.Equal("kickstart-storage-v2",manifest["Protocol"]!.GetValue<string>());
        Assert.Equal(StorageIdePartition.Layout,manifest["IdeLayout"]!.GetValue<string>());
    }

    [Fact]
    public async Task Kickstart31Sfs2IdeMatrixSelectsAll288ColdComparisons()
    {
        var arguments=Arguments("ks31","--Backend","A1200Ide","--A1200Kickstart","3.1",
            "--A1200Rom31",Path.Combine(root,"a1200-31[!].rom"),"--FileSystemProfile","Sfs2").ToList();
        // A targeted SFS2/3.1 run needs neither FFS nor either older ROM input.
        foreach(var name in new[]{"--OriginalFfsHandler","--A1200Rom","--LegacyRom"})
        { var index=arguments.IndexOf(name); arguments.RemoveRange(index,2); }
        var manifest=Read(await MatrixCommand.Prepare(arguments.ToArray()));
        Assert.Equal(new[]{"stock-ks31-ide-hdd","stock-ks31-ide-cf","fast-ks31-ide-hdd","fast-ks31-ide-cf"},
            manifest["Scenarios"]!.AsArray().Select(v=>v!.GetValue<string>()));
        Assert.Equal(new[]{"sfs2-csun","sfs2-trst"},manifest["Profiles"]!.AsArray().Select(v=>v!.GetValue<string>()));
        Assert.Equal(288,manifest["Scenarios"]!.AsArray().Count*manifest["Profiles"]!.AsArray().Count*
            manifest["Variants"]!.AsArray().Count*4*manifest["Trials"]!.GetValue<int>());
        Assert.Equal("",manifest["A1200Rom"]!.GetValue<string>());
        Assert.EndsWith("a1200-31[!].rom",manifest["A1200Rom31"]!.GetValue<string>());
    }

    [Fact]
    public async Task BothRomVersionsRetainTheHistoricalControlOnlyOnce()
    {
        var manifest=Read(await MatrixCommand.Prepare(Arguments("both-roms","--A1200Kickstart","Both",
            "--A1200Rom31",Path.Combine(root,"a1200-31[!].rom"))));
        var scenarios=manifest["Scenarios"]!.AsArray().Select(v=>v!.GetValue<string>()).ToArray();
        Assert.Equal(21,scenarios.Length); Assert.Equal(1,scenarios.Count(s=>s=="legacy-zero"));
        Assert.Equal(10,scenarios.Count(s=>s.Contains("-ks31-")));
    }

    [Theory]
    [InlineData("ZeroLatency","A1200Fast","Both","fast-zero")]
    [InlineData("TimedCopperHdf","All","Cf","stock-cf,fast-cf")]
    [InlineData("A1200Ide","A1200Stock","Hdd","stock-ide-hdd")]
    [InlineData("ZeroLatency","Legacy","Both","legacy-zero")]
    public async Task SelectorsKeepOnlyMatchingScenarios(string backend,string machine,string drive,string expected)
    {
        var prepared=await MatrixCommand.Prepare(Arguments("selected","--Backend",backend,"--MachineProfile",machine,
            "--DriveProfile",drive,"--PayloadCachePolicy","Bypass"));
        var manifest=Read(prepared);
        Assert.Equal(expected.Split(','),manifest["Scenarios"]!.AsArray().Select(v=>v!.GetValue<string>()));
        Assert.Equal(new[]{"original","bypass"},manifest["Variants"]!.AsArray().Select(v=>v!.GetValue<string>()));
    }

    [Fact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        var arguments=Arguments("immutable"); var prepared=await MatrixCommand.Prepare(arguments);
        var original=File.ReadAllText(prepared[1]);
        await Assert.ThrowsAsync<IOException>(()=>MatrixCommand.Prepare(arguments));
        Assert.Equal(original,File.ReadAllText(prepared[1]));
    }

    [Fact]
    public async Task UnsupportedCombinationDoesNotCreateOutput()
    {
        await Assert.ThrowsAsync<ArgumentException>(()=>MatrixCommand.Prepare(Arguments("invalid","--Backend","A1200Ide","--MachineProfile","Legacy")));
        Assert.False(Directory.Exists(Path.Combine(root,"invalid")));
    }

    [Fact]
    public async Task SmokeMustBeExplicitAndDoesNotLeakIntoFullRuns()
    {
        await MatrixCommand.Prepare(Arguments("smoke","--Smoke"));
        Assert.Equal("1",Environment.GetEnvironmentVariable("COPPERFS_STORAGE_SMOKE"));
        await MatrixCommand.Prepare(Arguments("full"));
        Assert.Null(Environment.GetEnvironmentVariable("COPPERFS_STORAGE_SMOKE"));
    }

    [Fact]
    public async Task PartialTimingOverridesAndProvenanceArePreserved()
    {
        var overrides=Path.Combine(root,"timing.json"); File.WriteAllText(overrides,"{\"Hdd\":{\"CommandMicroseconds\":123}}");
        var receipt=Path.Combine(root,"receipt.json"); File.WriteAllText(receipt,"{\"test\":true}");
        var prepared=await MatrixCommand.Prepare(Arguments("profiles","--TimingProfiles",overrides,"--ReferenceProvenance",receipt));
        Assert.Equal(123,Read(prepared)["Hdd"]!["CommandMicroseconds"]!.GetValue<int>());
        Assert.Equal(File.ReadAllText(receipt),File.ReadAllText(Path.Combine(root,"profiles/reference-provenance.json")));
    }

    [Theory]
    [InlineData("--Workers","17")]
    [InlineData("--Backend","unknown")]
    [InlineData("--Unknown","anything")]
    public async Task InvalidOptionsAreRejected(string key,string value)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(()=>MatrixCommand.Prepare(Arguments("bad-option",key,value)));
        Assert.False(Directory.Exists(Path.Combine(root,"bad-option")));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COPPERFS_STORAGE_SMOKE",savedSmoke);
        // This unique test-owned directory is resolved before recursive removal.
        var full=Path.GetFullPath(root); var temporary=Path.GetFullPath(Path.GetTempPath());
        if(!full.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)) throw new IOException("Test cleanup escaped its temporary root.");
        Directory.Delete(full,true);
    }
}
