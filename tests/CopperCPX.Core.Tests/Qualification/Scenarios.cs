using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopperCPX.Qualification;

// Host-only protocol; a driver must execute the operation, never derive its
// answer from the model or decoder. Keep the trace independent of object IDs.
public enum ActionKind { Create, Write, Resize, Read, Rename, Delete, Remount, HardLink }
public enum Outcome { Success, Missing, Exists }
public sealed record Operation(ActionKind Kind, string Name = "", string Target = "", int Offset = 0, byte[]? Data = null);
public sealed record Answer(Outcome Outcome, byte[]? Data = null);
public sealed record Scenario(uint Seed, int AllocationUnit, Operation[] Operations);
public interface IScenarioDriver : IDisposable
{
    string Name { get; }
    Answer Execute(Operation operation);
    void Verify(IReadOnlyDictionary<string, byte[]> files);
}
public sealed class ScenarioFailure(string driver, int index, string detail, Exception? inner = null)
    : Exception($"{driver}, operation {index}: {detail}", inner)
{
    public string Driver { get; } = driver;
    public int Index { get; } = index;
}

public sealed class FileModel
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string,uint> objects=new(StringComparer.Ordinal);
    private uint nextObject;
    public IReadOnlyDictionary<string, byte[]> Files => files;
    private void ReplaceContents(string name,byte[] contents)
    {
        uint id=objects[name];
        foreach(string alias in objects.Where(pair=>pair.Value==id).Select(pair=>pair.Key)) files[alias]=contents;
    }
    public Answer Execute(Operation op)
    {
        if (op.Kind == ActionKind.Remount) return new(Outcome.Success);
        if (op.Kind == ActionKind.Create)
        {
            if(objects.ContainsKey(op.Name)) ReplaceContents(op.Name,[]);
            else { objects.Add(op.Name,++nextObject); files.Add(op.Name,[]); }
            return new(Outcome.Success);
        }
        if (!files.TryGetValue(op.Name, out byte[]? bytes)) return new(Outcome.Missing);
        switch (op.Kind)
        {
            case ActionKind.Write:
                byte[] data = op.Data ?? throw new InvalidDataException("Missing write payload.");
                if (data.Length != 0)
                {
                    Array.Resize(ref bytes, Math.Max(bytes.Length, checked(op.Offset + data.Length)));
                    data.CopyTo(bytes, op.Offset); ReplaceContents(op.Name,bytes);
                }
                break;
            case ActionKind.Resize: Array.Resize(ref bytes, op.Offset); ReplaceContents(op.Name,bytes); break;
            case ActionKind.Read:
                return new(Outcome.Success, bytes.AsSpan(Math.Min(op.Offset, bytes.Length), Math.Min(op.Data?.Length ?? 0, Math.Max(0, bytes.Length - op.Offset))).ToArray());
            case ActionKind.Rename:
                if (op.Name == op.Target) break;
                if (files.ContainsKey(op.Target)) return new(Outcome.Exists);
                files.Add(op.Target, bytes); files.Remove(op.Name); objects.Add(op.Target,objects[op.Name]); objects.Remove(op.Name); break;
            case ActionKind.Delete: files.Remove(op.Name); objects.Remove(op.Name); break;
            case ActionKind.HardLink:
                if(files.ContainsKey(op.Target)) return new(Outcome.Exists);
                files.Add(op.Target,bytes); objects.Add(op.Target,objects[op.Name]); break;
            default: throw new InvalidDataException("Unknown scenario operation.");
        }
        return new(Outcome.Success);
    }
}

public static class Scenarios
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    // Specified PRNG: saved seeds do not depend on a runtime's Random algorithm.
    private static uint Next(ref uint state) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
    public static Scenario Generate(uint seed, int count, int unit)
    {
        if (seed == 0 || count < 1 || count > 1_048_576) throw new ArgumentOutOfRangeException(nameof(seed));
        uint random = seed; var model = new FileModel(); var result = new List<Operation>();
        int[] edges = [0, 1, 159, 160, 161, 511, 512, 513, 1023, 1024, 4095, 4096, 4097, 8192];
        for (int n = 0; n < count; n++)
        {
            string name = $"f{Next(ref random) % 24:D2}é";
            ActionKind kind = (ActionKind)(Next(ref random) % 8);
            int offset = edges[Next(ref random) % edges.Length]; byte[]? data = null;
            if (kind is ActionKind.Write or ActionKind.Read)
            {
                // Classic DOS Seek refuses positions beyond EOF. The common
                // trace stays in that ABI; direct sparse writes have core tests.
                if (model.Files.TryGetValue(name, out var file)) offset = Math.Min(offset, file.Length);
                data = new byte[edges[Next(ref random) % 10]];
                for (int b = 0; b < data.Length; b++) data[b] = (byte)Next(ref random);
            }
            var op = new Operation(kind, name, $"f{Next(ref random) % 24:D2}é", offset, data);
            model.Execute(op); result.Add(op);
        }
        return new(seed, unit, result.ToArray());
    }
    public static void Run(Scenario scenario, params IScenarioDriver[] drivers)
    {
        var model = new FileModel();
        try
        {
            for (int index = 0; index < scenario.Operations.Length; index++)
            {
                Operation op = scenario.Operations[index]; Answer expected = model.Execute(op);
                foreach (var driver in drivers)
                {
                    try
                    {
                        Answer actual = driver.Execute(op);
                        if (actual.Outcome != expected.Outcome || !(actual.Data ?? []).AsSpan().SequenceEqual(expected.Data ?? []))
                            throw new InvalidDataException($"{op.Kind}: expected {expected.Outcome}/{expected.Data?.Length}, got {actual.Outcome}/{actual.Data?.Length}");
                        if ((index & 15) == 15 || op.Kind == ActionKind.Remount || index + 1 == scenario.Operations.Length) driver.Verify(model.Files);
                    }
                    catch (Exception e) { throw new ScenarioFailure(driver.Name, index, e.Message, e); }
                }
            }
        }
        finally { foreach (var driver in drivers) driver.Dispose(); }
    }
    // Delta debugging preserves the failing driver; all candidate subsequences
    // are independently replayed from a fresh image. Missing-name errors are
    // valid protocol results, so deleted prerequisites do not invalidate a trace.
    public static Scenario Minimise(Scenario source, Func<Scenario, bool> reproduces, int attempts = 128)
    {
        var current = source; int width = Math.Max(1, current.Operations.Length / 2);
        while (width > 0 && attempts > 0)
        {
            bool changed = false;
            for (int at = 0; at < current.Operations.Length && attempts-- > 0; at += width)
            {
                var candidate = current with { Operations = current.Operations.Take(at).Concat(current.Operations.Skip(at + width)).ToArray() };
                if (candidate.Operations.Length == 0 || !reproduces(candidate)) continue;
                current = candidate; changed = true; break;
            }
            if (!changed) width /= 2;
        }
        return current;
    }
}
