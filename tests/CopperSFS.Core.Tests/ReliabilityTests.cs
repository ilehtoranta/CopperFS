using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amiga;
using CopperSFS.Check;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class ReliabilityTests(ITestOutputHelper output)
{
    private sealed record Mutation(string Kind, uint Node = 3, string Name = "New", uint Position = 0,
        uint Size = 0, byte[]? Data = null);
    private sealed record Expected(string Path, uint Node, bool Directory, byte[] Data,
        uint Protection = 15, uint Modified = 86461, string Comment = "", byte Flags = 0)
    {
        internal ImageEntry Entry => new(Path, Node, Directory, (uint)Data.Length, Protection, Modified,
            0, 0, Flags, Comment, Directory ? null : Convert.ToHexString(SHA256.HashData(Data)));
    }
    private static AmigaSmartFileSystemWriteCore.Work Work() => new()
    {
        Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024), Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048)
    };
    private static List<Expected> InitialModel() =>
    [
        new("/", 1, true, [], Flags: 128),
        new("/Docs", 2, true, [], Flags: 128),
        new("/Hello", 3, false, Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)).ToArray(), Comment: "greeting"),
        new("/Docs/Nested", 4, false, Encoding.ASCII.GetBytes("hello"))
    ];
    private static string Manifest(IEnumerable<ImageEntry> entries) =>
        JsonSerializer.Serialize(entries.OrderBy(e => e.Path, StringComparer.Ordinal));
    private static string Manifest(List<Expected> model) => Manifest(model.Select(e => e.Entry));
    private static void Validate(byte[] image, List<Expected> model) =>
        Assert.Equal(Manifest(model), Manifest(Inspect(image).Entries));

    private static bool Execute(ref ReliabilityDisk disk, Mutation mutation, out uint created, out DOS.Error error)
    {
        var work = Work(); created = 0;
        var request = new AmigaSmartFileSystemWriteCore.Request
        {
            Node = mutation.Node, Directory = 1, Name = disk.Name(mutation.Name), Length = (uint)mutation.Name.Length,
            Position = mutation.Position, Buffer = APTR.FromPointer(4000)
        };
        if (mutation.Data is not null) mutation.Data.CopyTo(disk.Ram, 4000);
        bool success;
        switch (mutation.Kind)
        {
            case "create": case "mkdir":
                success = AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, mutation.Kind == "mkdir");
                created = request.Node; break;
            case "write":
                request.Length = (uint)mutation.Data!.Length;
                success = AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual);
                if (success) Assert.Equal(request.Length, actual);
                break;
            case "resize": success = AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, mutation.Node, mutation.Size); break;
            case "truncate": success = AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, mutation.Node); break;
            case "remove": success = AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, mutation.Node); break;
            case "rename": success = AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request); break;
            case "comment":
                request.Length = (uint)mutation.Data!.Length;
                success = AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment); break;
            case "protect":
                request.Position = mutation.Size;
                success = AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetProtection); break;
            case "date":
                disk.WriteUInt32(request.Buffer, 0, 2); disk.WriteUInt32(request.Buffer, 4, 3); disk.WriteUInt32(request.Buffer, 8, 100);
                success = AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetDate); break;
            default: throw new ArgumentException(mutation.Kind);
        }
        error = work.Error; return success;
    }
    private static List<Expected> Update(List<Expected> model, Mutation mutation, uint created)
    {
        var updated = model.ToList();
        if (mutation.Kind is "create" or "mkdir")
        {
            updated.Add(new("/" + mutation.Name, created, mutation.Kind == "mkdir", [], Modified: 0,
                Flags: mutation.Kind == "mkdir" ? (byte)128 : (byte)0));
            return updated;
        }
        var index = updated.FindIndex(e => e.Node == mutation.Node);
        var current = updated[index];
        switch (mutation.Kind)
        {
            case "write":
                var data = current.Data.ToArray();
                Array.Resize(ref data, Math.Max(data.Length, (int)mutation.Position + mutation.Data!.Length));
                mutation.Data.CopyTo(data, (int)mutation.Position); updated[index] = current with { Data = data }; break;
            case "resize": case "truncate":
                var resized = current.Data.ToArray(); Array.Resize(ref resized, mutation.Kind == "truncate" ? 0 : (int)mutation.Size);
                updated[index] = current with { Data = resized }; break;
            case "remove": updated.RemoveAt(index); break;
            case "rename":
                for (var i = 0; i < updated.Count; i++)
                    if (updated[i].Path == current.Path || updated[i].Path.StartsWith(current.Path + "/", StringComparison.Ordinal))
                        updated[i] = updated[i] with { Path = "/" + mutation.Name + updated[i].Path[current.Path.Length..] };
                break;
            case "comment": updated[index] = current with { Comment = Encoding.Latin1.GetString(mutation.Data!) }; break;
            case "protect": updated[index] = current with { Protection = mutation.Size ^ 15 }; break;
            case "date": updated[index] = current with { Modified = 2 * 86400 + 3 * 60 + 2 }; break;
        }
        return updated;
    }
    private static void Commit(ref ReliabilityDisk disk, ref List<Expected> model, Mutation mutation)
    {
        Assert.True(Execute(ref disk, mutation, out var created, out var error), $"{mutation}: {error}");
        model = Update(model, mutation, created);
        Validate(disk.Snapshot(), model);
    }
    private static byte[] Pattern(int length, int seed = 7) => Enumerable.Range(0, length).Select(i => (byte)(i * 29 + seed)).ToArray();
    // Additional research scenarios are tracked in docs/research-port.md.
    public static IEnumerable<object[]> Scenarios => new[]
    {
        "create", "mkdir", "overwrite", "append", "shrink", "grow", "truncate", "delete", "rename",
        "rename-directory", "comment", "protect", "date", "rmdir", "directory-growth",
        "admin-growth", "extent-growth", "near-full"
    }
    .Select(name => new object[] { name });

    private static (byte[] Image, List<Expected> Before, Mutation Mutation) Prepare(string scenario)
    {
        var image = Image(); var model = InitialModel(); Validate(image, model);
        if (scenario == "admin-growth") return PrepareAdminGrowth(image, model);
        if (scenario == "extent-growth") return PrepareExtentGrowth(image, model);
        if (scenario == "near-full") return PrepareNearFull(image, model);
        if (scenario is "rmdir" or "directory-growth")
        {
            var disk = new ReliabilityDisk(image);
            if (scenario == "rmdir")
            {
                Commit(ref disk, ref model, new("mkdir", Name: "Empty"));
                return (disk.Snapshot(), model, new("remove", Node: model[^1].Node));
            }
            // Research commit 4a902a1 fills the first root directory container with long names.
            for (var i = 0; i < 3; i++)
                Commit(ref disk, ref model, new("create", Name: $"{i}" + new string('x', 99)));
            return (disk.Snapshot(), model, new("create", Name: new string('y', 100)));
        }
        Mutation mutation = scenario switch
        {
            "create" => new("create"),
            "mkdir" => new("mkdir"),
            "grow" => new("resize", Size: 2301),
            "rename-directory" => new("rename", Node: 2, Name: "Moved"),
            "comment" => new("comment", Data: Encoding.Latin1.GetBytes("a longer replacement comment")),
            "protect" => new("protect", Size: 0x20),
            "date" => new("date"),
            "overwrite" => new("write", Position: 399, Data: Pattern(733)),
            "append" => new("write", Position: 1300, Data: Pattern(1001)),
            "shrink" => new("resize", Size: 513),
            "truncate" => new("truncate"),
            "delete" => new("remove"),
            "rename" => new("rename", Name: "Docs/Renamed"),
            _ => throw new ArgumentException("Unselected research scenario", nameof(scenario))
        };
        return (image, model, mutation);
    }

    private static (byte[] Image, List<Expected> Before, Mutation Mutation) PrepareAdminGrowth(
        byte[] image, List<Expected> model)
    {
        var disk = new ReliabilityDisk(image);
        // Research fixture 4a902a1 seeds two 32-block ranges in ADMC block 10.
        const int thirdRange = 24 + 2 * 8;
        for (var i = 0; i < 100; i++)
        {
            var directory = $"D{i:D3}";
            foreach (var mutation in new[] { new Mutation("mkdir", Name: directory),
                         new Mutation("create", Name: directory + "/Child") })
            {
                var before = disk.Snapshot();
                var previous = model;
                Commit(ref disk, ref model, mutation);
                var after = disk.Snapshot();
                if (U32(Block(after, 10), thirdRange) == 0) continue;
                Assert.Equal(uint.MaxValue, U32(Block(after, 10), 28));
                Assert.Equal(uint.MaxValue, U32(Block(after, 10), 36));
                Assert.NotEqual(0u, U32(Block(after, 10), thirdRange + 4));

                Assert.Equal(0u, U32(Block(before, 10), thirdRange));
                var reopened = new ReliabilityDisk(before);
                Assert.True(Recover(ref reopened));
                Validate(reopened.Snapshot(), previous);
                var expanded = new ReliabilityDisk(after);
                Assert.True(Recover(ref expanded));
                Validate(expanded.Snapshot(), model);
                return (before, previous, mutation);
            }
        }
        throw new InvalidOperationException("No third admin range allocated.");
    }

    private static (byte[] Image, List<Expected> Before, Mutation Mutation) PrepareExtentGrowth(
        byte[] image, List<Expected> model)
    {
        // Research commit 4a902a1: each nonempty file needs its own extent.
        const int rootBlock = 5, headerBytes = 16, leafStride = 14;
        const int capacity = (Bytes - headerBytes) / leafStride;
        var disk = new ReliabilityDisk(image);
        var initialCount = BinaryPrimitives.ReadUInt16BigEndian(Block(image, rootBlock)[12..]);
        for (var i = initialCount; i < capacity; i++)
        {
            Commit(ref disk, ref model, new("create", Name: $"F{i:D3}"));
            Commit(ref disk, ref model, new("write", Node: model[^1].Node, Data: Pattern(1, i)));
        }
        Commit(ref disk, ref model, new("create", Name: "Extra"));
        var before = disk.Snapshot();
        var reopened = new ReliabilityDisk(before);
        Assert.True(Recover(ref reopened));
        Validate(reopened.Snapshot(), model);
        var leaf = Block(before, rootBlock);
        Assert.Equal(1, leaf[14]);
        Assert.Equal(leafStride, leaf[15]);
        Assert.Equal(capacity, BinaryPrimitives.ReadUInt16BigEndian(leaf[12..]));

        var mutation = new Mutation("write", Node: model[^1].Node, Data: Pattern(1));
        var expandedModel = model;
        Commit(ref reopened, ref expandedModel, mutation);
        var expanded = new ReliabilityDisk(reopened.Snapshot());
        Assert.True(Recover(ref expanded));
        var after = expanded.Snapshot();
        Validate(after, expandedModel);
        var branch = Block(after, rootBlock);
        Assert.Equal(0, branch[14]);
        Assert.Equal(8, branch[15]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(branch[12..]));
        var left = U32(branch, 20);
        var right = U32(branch, 28);
        Assert.NotEqual(left, right);
        var total = 0;
        foreach (var child in new[] { left, right })
        {
            var childLeaf = Block(after, checked((int)child));
            Assert.Equal(1, childLeaf[14]);
            Assert.Equal(leafStride, childLeaf[15]);
            total += BinaryPrimitives.ReadUInt16BigEndian(childLeaf[12..]);
        }
        Assert.Equal(capacity + 1, total);
        return (before, model, mutation);
    }

    private const uint NearFullFreeBlocks = 20;

    private static (byte[] Image, List<Expected> Before, Mutation Mutation) PrepareNearFull(
        byte[] image, List<Expected> model)
    {
        // Research fixture 4a902a1 fills /Hello one committed block at a time.

        var disk = new ReliabilityDisk(image);
        var position = (uint)model.Single(entry => entry.Path == "/Hello").Data.Length;
        var free = Inspect(image).FreeBlocks;
        Assert.True(free > NearFullFreeBlocks);
        while (free > NearFullFreeBlocks)
        {
            Commit(ref disk, ref model, new("write", Position: position, Data: Pattern(Bytes)));
            position += Bytes;
            var next = Inspect(disk.Snapshot()).FreeBlocks;
            Assert.Equal(free - 1, next);
            free = next;
        }
        var before = disk.Snapshot();
        var reopened = new ReliabilityDisk(before);
        Assert.True(Recover(ref reopened));
        var stable = reopened.Snapshot();
        Validate(stable, model);
        Assert.Equal(NearFullFreeBlocks, Inspect(stable).FreeBlocks);
        Assert.Equal(before, stable);
        return (stable, model, new("write", Position: position, Data: Pattern(8192)));
    }

    [Fact]
    public void NearFullAppendReopensWithConsistentFreeSpace()
    {
        var (image, model, mutation) = Prepare("near-full");
        var disk = new ReliabilityDisk(image);
        Assert.False(Execute(ref disk, mutation, out _, out var error));
        Assert.Equal(DOS.Error.DiskFull, error);
        var reopened = new ReliabilityDisk(disk.Snapshot());
        Assert.True(Recover(ref reopened));
        var recovered = reopened.Snapshot();
        Validate(recovered, model);
        Assert.Equal(NearFullFreeBlocks, Inspect(recovered).FreeBlocks);
    }

    private static bool Recover(ref ReliabilityDisk disk)
    {
        var work = Work(); return AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work);
    }
    private sealed class ObservedImage(byte[] image, ReliabilityDisk disk) : MemoryStream(image, writable: false)
    {
        public override int Read(Span<byte> buffer)
        {
            var start = Position;
            var count = base.Read(buffer);
            for (var block = start / Bytes; block * Bytes < start + count && count > 0; block++)
                disk.ObserveOriginal((uint)block);
            return count;
        }
    }

    internal static void CheckRecovered(byte[] image, string before, string after, string context, HashSet<uint>? reads = null, uint? expectedFree = null)
    {
        try
        {
            var disk = new ReliabilityDisk(image, reads);
            Assert.True(Recover(ref disk), $"Recovery failed: {context}");
            var stable = disk.Snapshot();
            using var observed = reads is null ? null : new ObservedImage(stable, disk);
            var report = observed is null ? Inspect(stable) : SfsImageChecker.Check(observed);
            if (expectedFree.HasValue) Assert.Equal(expectedFree.Value, report.FreeBlocks);
            var manifest = Manifest(report.Entries);
            Assert.True(manifest == before || manifest == after, $"Mixed or damaged volume: {context}\n{manifest}");
            var writes = disk.Writes;
            Assert.True(Recover(ref disk), context);
            if (reads is not null) Assert.Equal(writes, disk.Writes);
            Assert.Equal(stable, disk.Snapshot());
        }
        catch
        {
            SaveFailure(image, context, before, after);
            throw;
        }
    }
    private static void SaveFailure(byte[] image, string context, string before, string after)
    {
        var destination = Environment.GetEnvironmentVariable("COPPERSFS_RELIABILITY_ARTIFACTS");
        if (string.IsNullOrEmpty(destination)) return;
        Directory.CreateDirectory(destination);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)))[..16];
        File.WriteAllBytes(Path.Combine(destination, id + ".img"), image);
        File.WriteAllText(Path.Combine(destination, id + ".json"), JsonSerializer.Serialize(new { context, before, after }));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EveryMutationSurvivesAllWriteAndFlushCutsAndIoFailures(string scenario)
    {
        var (image, model, mutation) = Prepare(scenario);
        var before = Manifest(model); var probe = new ReliabilityDisk(image);
        var success = Execute(ref probe, mutation, out var created, out var error);
        if (scenario != "near-full") Assert.True(success, $"{scenario} failed after reopen: {error}");
        var after = before;
        if (success)
        {
            if (scenario == "directory-growth")
            {
                // The fixture root object points to its first OBJC at offset 40.
                // Its next link at offset 16 gains a second container on this insert.
                var first = checked((int)U32(Block(image, 2), 40));
                Assert.Equal(0u, U32(Block(image, first), 16));
                Assert.NotEqual(0u, U32(Block(probe.Snapshot(), first), 16));
            }
            var updated = Update(model, mutation, created); Validate(probe.Snapshot(), updated); after = Manifest(updated);
        }
        else Assert.Equal(DOS.Error.DiskFull, error);
        var images = 0; byte[]? dirty = null;
        void Verify(ReliabilityDisk disk, string expected, string fault, bool keepDirty = false)
        {
            // Vary all versions of the input blocks that recovery or the checker reads.
            // Newly observed reads expand the Cartesian product until it is stable.
            HashSet<uint>? reads = scenario == "near-full" ? new() { 4 } : null;
            int previous;
            do
            {
                previous = reads?.Count ?? 0;
                foreach (var saved in disk.CrashImages(reads))
                {
                    CheckRecovered(saved, expected, after, $"{scenario}, {fault}, image {images++}", reads,
                        scenario == "near-full" ? NearFullFreeBlocks : null);
                    if (keepDirty && U32(Block(saved, 4), 0) == 0x54524641) dirty = saved;
                }
            } while (reads is not null && reads.Count > previous);
        }
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new ReliabilityDisk(image) { CutAfterEvent = cut };
            Assert.Throws<SimulatedPowerCut>(() => Execute(ref disk, mutation, out _, out _));
            Verify(disk, before, $"cut {cut}", keepDirty: true);
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new ReliabilityDisk(image) { FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(Execute(ref disk, mutation, out _, out _), $"Missed I/O failure: {scenario}, {fail}");
                Verify(disk, before, $"I/O failure {fail}, persistent {persistent}");
            }
            for (var fail = 1; fail <= probe.Reads; fail++)
            {
                var disk = new ReliabilityDisk(image) { FailAtRead = fail, PersistentFailure = persistent };
                var completed = Execute(ref disk, mutation, out _, out _);
                Verify(disk, completed ? after : before, $"read failure {fail}, persistent {persistent}");
            }
        }
        Assert.NotNull(dirty);
        var recoveryImages = CheckRecoveryCuts(dirty, before, after, scenario);
        output.WriteLine($"{scenario}: {probe.Writes} writes, {probe.Events} write/flush points, {probe.Reads} reads; {images} mutation crash images, {recoveryImages} recovery crash images.");
    }
    private static int CheckRecoveryCuts(byte[] dirty, string before, string after, string scenario)
    {
        var probe = new ReliabilityDisk(dirty); Assert.True(Recover(ref probe));
        var count = 0;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new ReliabilityDisk(dirty) { CutAfterEvent = cut };
            Assert.Throws<SimulatedPowerCut>(() => Recover(ref disk));
            foreach (var saved in disk.CrashImages())
                CheckRecovered(saved, before, after, $"{scenario}, recovery cut {cut}, image {count++}");
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new ReliabilityDisk(dirty) { FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(Recover(ref disk));
                foreach (var saved in disk.CrashImages())
                    CheckRecovered(saved, before, after, $"{scenario}, recovery I/O failure {fail}, image {count++}");
            }
            for (var fail = 1; fail <= probe.Reads; fail++)
            {
                var disk = new ReliabilityDisk(dirty) { FailAtRead = fail, PersistentFailure = persistent };
                // A failed root read may fall back to the other root copy.
                Recover(ref disk);
                foreach (var saved in disk.CrashImages())
                    CheckRecovered(saved, before, after, $"{scenario}, recovery read failure {fail}, image {count++}");
            }
        }
        return count;
    }

    [Theory]
    [InlineData("overwrite")]
    [InlineData("append")]
    [InlineData("rename")]
    [InlineData("delete")]
    public void TornWriteEitherRecoversWholeVolumeOrRefusesMount(string scenario)
    {
        var (image, model, mutation) = Prepare(scenario);
        var probe = new ReliabilityDisk(image);
        Assert.True(Execute(ref probe, mutation, out var created, out _));
        var before = Manifest(model); var after = Manifest(Update(model, mutation, created));
        var recoveredCount = 0; var refused = 0;
        for (var write = 1; write <= probe.Writes; write++)
            foreach (var prefix in new[] { 1, 4, 8, 12, 16, 256, 511 })
            {
                var disk = new ReliabilityDisk(image) { TearAtWrite = write, TearBytes = prefix };
                Assert.Throws<SimulatedPowerCut>(() => Execute(ref disk, mutation, out _, out _));
                var restarted = new ReliabilityDisk(disk.Snapshot());
                if (Recover(ref restarted))
                {
                    CheckRecovered(restarted.Snapshot(), before, after, $"{scenario}, torn write {write}, prefix {prefix}");
                    recoveredCount++;
                }
                else
                {
                    Assert.False(AmigaSmartFileSystemCore.Mount(ref restarted, APTR.FromPointer(512)));
                    Assert.Throws<InvalidDataException>(() => Inspect(restarted.Snapshot()));
                    refused++;
                }
            }
        output.WriteLine($"{scenario}: {recoveredCount} torn-write cases recovered, {refused} refused mount.");
    }

    [Theory]
    [InlineData(104729)]
    [InlineData(130363)]
    [InlineData(155921)]
    public void SeededRandomWorkloadMatchesReferenceModelAcrossRestarts(int seed)
    {
        var random = new Random(seed); var disk = new ReliabilityDisk(Image()); var model = InitialModel();
        for (var step = 0; step < 300; step++)
        {
            var files = model.Where(e => !e.Directory).ToArray();
            var target = files[random.Next(files.Length)];
            var name = $"R{step:D4}";
            var mutation = random.Next(8) switch
            {
                0 when files.Length < 15 => new Mutation("create", Name: name),
                1 => new("write", Node: target.Node, Position: (uint)random.Next(target.Data.Length + 1), Data: Pattern(random.Next(1, 1000), step)),
                2 => new("resize", Node: target.Node, Size: (uint)random.Next(2500)),
                3 when files.Length > 2 => new("remove", Node: target.Node),
                4 => new("rename", Node: target.Node, Name: (random.Next(2) == 0 ? "Docs/" : "") + name),
                5 => new("comment", Node: target.Node, Data: Encoding.Latin1.GetBytes(name)),
                6 => new("truncate", Node: target.Node),
                _ => new("date", Node: target.Node)
            };
            var committed = disk.Snapshot(); var probe = new ReliabilityDisk(committed);
            var succeeded = Execute(ref probe, mutation, out var created, out var error);
            var nextModel = succeeded ? Update(model, mutation, created) : model;
            if (!succeeded) Assert.Equal(DOS.Error.DiskFull, error);
            if (step % 3 == 0 && probe.Events > 0)
            {
                var cut = random.Next(1, probe.Events + 1);
                var interrupted = new ReliabilityDisk(committed) { CutAfterEvent = cut };
                Assert.Throws<SimulatedPowerCut>(() => Execute(ref interrupted, mutation, out _, out _));
                var choices = interrupted.CrashImages().ToArray();
                var saved = choices[random.Next(choices.Length)];
                var context = $"Seed {seed}, step {step}, cut {cut}, {mutation}";
                CheckRecovered(saved, Manifest(model), Manifest(nextModel), context);
                disk = new ReliabilityDisk(saved);
                Assert.True(Recover(ref disk), context);
                var actual = Manifest(Inspect(disk.Snapshot()).Entries);
                Assert.True(actual == Manifest(model) || actual == Manifest(nextModel),
                    $"Seed {seed}, step {step}, cut {cut}, {mutation}\nBefore: {Manifest(model)}\nAfter: {Manifest(nextModel)}\nActual: {actual}");
                if (actual == Manifest(nextModel)) model = nextModel;
            }
            else { disk = new ReliabilityDisk(probe.Snapshot()); model = nextModel; }
            Validate(disk.Snapshot(), model);
        }
        output.WriteLine($"Seed {seed}: 300 modeled operations, up to 100 power cuts, full manifest checked after every step.");
    }
}
