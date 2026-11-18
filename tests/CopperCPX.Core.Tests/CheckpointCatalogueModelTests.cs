using System.Reflection;

namespace CopperCPX.Tests;

public class CheckpointCatalogueModelTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void UpdatesLookupsAndRemovalsMatchBoundedDictionaryForNarrowAndWideKeys(bool wide)
    {
        var ram = new Ram(new byte[1024]);
        Type type = typeof(CpxEngine<Ram>).Assembly.GetType("CopperCPX.CpxCheckpointCatalogue`1")!.MakeGenericType(typeof(Ram));
        object catalogue = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [ram, 32u], null)!;
        var clear = type.GetMethod("Clear", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var set = type.GetMethod("Set", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var lookup = type.GetMethod("Lookup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var valid = type.GetMethod("Valid", BindingFlags.Instance | BindingFlags.NonPublic)!;
        clear.Invoke(catalogue, null);
        var expected = new Dictionary<ulong, uint>(); var random = new Random(9713);
        ulong[] keys = Enumerable.Range(0, 96).Select(n => wide ? ((ulong)n << 53) + (uint)(n * 97 + 3) : (ulong)(n * 97 + 3)).ToArray();
        keys[0] = 0; keys[^1] = (1UL << 62) - 1;
        for (int turn = 0; turn < 1200; turn++)
        {
            ulong key = keys[random.Next(keys.Length)]; uint flags = (uint)random.Next(4);
            bool allowed = flags == 0 || expected.ContainsKey(key) || expected.Count < 64;
            Assert.Equal(allowed, (bool)set.Invoke(catalogue, [key, flags])!);
            if (allowed) { if (flags == 0) expected.Remove(key); else expected[key] = flags; }
            Assert.Equal((uint)expected.Count, ram.Read32(32));
            Assert.True((bool)valid.Invoke(catalogue, [1UL << 62, true])!);
            foreach (ulong candidate in keys)
                Assert.Equal(expected.GetValueOrDefault(candidate), (uint)lookup.Invoke(catalogue, [candidate])!);
            Assert.Equal(0u, (uint)lookup.Invoke(catalogue, [ulong.MaxValue])!);
            Assert.True(ram.Bytes.AsSpan(0, 32).IndexOfAnyExcept((byte)0) < 0);
            Assert.True(ram.Bytes.AsSpan(560).IndexOfAnyExcept((byte)0) < 0);
        }
    }
}
