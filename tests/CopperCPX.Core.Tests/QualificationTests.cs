using CopperCPX.Qualification;
using System.Text.Json;

namespace CopperCPX.Tests;

public class QualificationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static void Run(Scenario scenario) => Scenarios.Run(scenario,
        new ReferenceDriver(scenario.AllocationUnit), new EngineDriver(scenario.AllocationUnit), new DispatcherDriver(scenario.AllocationUnit));

    [Fact, Trait("Qualification", "Generated")]
    public void SeededSequencesMatchModelReferenceEngineAndDos()
    {
        string? replay = Environment.GetEnvironmentVariable("COPPERCPX_REPLAY");
        if (!string.IsNullOrEmpty(replay))
        {
            var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(replay), Scenarios.Json) ?? throw new InvalidDataException("Empty replay.");
            Run(scenario); output.WriteLine($"Replayed {scenario.Operations.Length} operations, seed {scenario.Seed}."); return;
        }
        string tier = Environment.GetEnvironmentVariable("COPPERCPX_TIER") ?? "fast";
        (int seeds, int operations) = tier switch { "fast" => (4, 128), "integration" => (32, 512), "campaign" => (1024, 1024), _ => throw new InvalidDataException($"Unknown tier {tier}") };
        string? explicitSeed = Environment.GetEnvironmentVariable("COPPERCPX_SEED");
        uint first = explicitSeed is null ? 0xc0ffeeu : uint.Parse(explicitSeed, System.Globalization.CultureInfo.InvariantCulture);
        if (explicitSeed is not null) seeds = 1;
        for (int n = 0; n < seeds; n++)
        {
            uint seed = unchecked(first + (uint)n); if (seed == 0) seed = 1;
            var scenario = Scenarios.Generate(seed, operations, 512 << (n % 4));
            try { Run(scenario); }
            catch (ScenarioFailure failure)
            {
                string directory = Environment.GetEnvironmentVariable("COPPERCPX_ARTIFACTS") ?? Path.Combine(RepositoryRoot(), "artifacts", "cpx4-qualification");
                Directory.CreateDirectory(directory); string stem = Path.Combine(directory, $"failure-{seed}-{scenario.AllocationUnit}");
                File.WriteAllText(stem + ".json", JsonSerializer.Serialize(scenario, Scenarios.Json));
                File.WriteAllText(stem + ".txt", failure.ToString());
                var minimal = Scenarios.Minimise(scenario, candidate =>
                {
                    try { Run(candidate); return false; }
                    catch (ScenarioFailure f) { return f.Driver == failure.Driver && f.InnerException?.GetType() == failure.InnerException?.GetType() && f.InnerException?.Message == failure.InnerException?.Message; }
                });
                File.WriteAllText(stem + ".minimal.json", JsonSerializer.Serialize(minimal, Scenarios.Json));
                throw new InvalidDataException($"Seed {seed}, AU {scenario.AllocationUnit}, replay {stem}.minimal.json", failure);
            }
            output.WriteLine($"Seed {seed}, AU {scenario.AllocationUnit}: {operations} operations passed all three drivers plus ownership checks.");
            string? progressDirectory=Environment.GetEnvironmentVariable("COPPERCPX_ARTIFACTS");
            if(!string.IsNullOrEmpty(progressDirectory))
            {
                Directory.CreateDirectory(progressDirectory);
                File.WriteAllText(Path.Combine(progressDirectory,"generated-sequences.progress.json"),JsonSerializer.Serialize(new
                { tier,passedSeeds=n+1,requestedSeeds=seeds,lastSeed=seed,operationsPerSeed=operations,allocationUnit=scenario.AllocationUnit,
                  complete=n+1==seeds,updatedUtc=DateTime.UtcNow },Scenarios.Json));
            }
        }
    }

    [Fact] public void ScenarioSerializationAndMinimisationKeepAReproducibleFailure()
    {
        var scenario = new Scenario(42, 512, [new(ActionKind.Create, "a"), new(ActionKind.Create, "b"), new(ActionKind.Delete, "a"), new(ActionKind.Remount)]);
        var restored = JsonSerializer.Deserialize<Scenario>(JsonSerializer.Serialize(scenario, Scenarios.Json), Scenarios.Json)!;
        var minimal = Scenarios.Minimise(restored, s => s.Operations.Any(op => op.Kind == ActionKind.Delete));
        Assert.Single(minimal.Operations); Assert.Equal(ActionKind.Delete, minimal.Operations[0].Kind);
        Assert.Equal(Scenarios.Generate(42, 128, 512).Operations, Scenarios.Generate(42, 128, 512).Operations, new OperationComparer());
    }
    [Fact] public void SharedObjectModelAndAllDriversPreserveAliasesAcrossMutation()
    {
        Run(new(99,512,[new(ActionKind.Create,"a"),new(ActionKind.Create,"unrelated"),new(ActionKind.HardLink,"a","b"),
            new(ActionKind.Write,"a",Data:[1,2,3]),new(ActionKind.Read,"b",Data:new byte[4]),new(ActionKind.Resize,"b",Offset:700),
            new(ActionKind.Remount),new(ActionKind.Create,"a"),new(ActionKind.Read,"b",Data:new byte[1]),
            new(ActionKind.Write,"b",Data:[42]),new(ActionKind.Rename,"a","renamed"),new(ActionKind.Delete,"b"),
            new(ActionKind.Read,"renamed",Data:new byte[1]),new(ActionKind.Delete,"renamed"),new(ActionKind.Remount)]));
    }
    private sealed class OperationComparer : IEqualityComparer<Operation>
    {
        public bool Equals(Operation? x, Operation? y) => JsonSerializer.Serialize(x, Scenarios.Json) == JsonSerializer.Serialize(y, Scenarios.Json);
        public int GetHashCode(Operation obj) => 0;
    }
    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
            if (File.Exists(Path.Combine(at.FullName, "CopperFS.slnx"))) return at.FullName;
        throw new DirectoryNotFoundException("CopperFS repository root not found.");
    }
}
