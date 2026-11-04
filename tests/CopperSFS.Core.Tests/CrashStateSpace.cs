using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CopperSFS.Tests;

// Immutable Cartesian product of the durable block value and every accepted
// unflushed version. Containment is checked with actual bytes, never hashes alone.
internal sealed class CrashStateSpace
{
    private readonly byte[] durable;
    private readonly int blockBytes;
    private readonly SortedDictionary<uint, byte[][]> domains = new();
    internal long LogicalCount { get; }
    internal long ImageCount { get; }
    internal string BaseHash { get; }
    internal string Id { get; }

    internal CrashStateSpace(byte[] image, int bytes, IEnumerable<KeyValuePair<uint, List<byte[]>>> pending)
    {
        if (bytes < 1 || image.Length % bytes != 0) throw new ArgumentException("Invalid crash-space geometry.");
        durable = image.ToArray(); blockBytes = bytes;
        long logical = 1, distinct = 1;
        foreach (var (block, versions) in pending)
        {
            if (block >= durable.Length / bytes || domains.ContainsKey(block)) throw new ArgumentException("Invalid/duplicate pending block.");
            var values = new List<byte[]> { durable.AsSpan(checked((int)block * bytes), bytes).ToArray() };
            logical = checked(logical * (versions.Count + 1));
            foreach (var value in versions)
            {
                if (value.Length != bytes) throw new ArgumentException("Invalid pending block size.");
                if (!values.Any(v => v.AsSpan().SequenceEqual(value))) values.Add(value.ToArray());
            }
            values.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
            if (values.Count > 1) domains.Add(block, values.ToArray());
            distinct = checked(distinct * values.Count);
        }
        LogicalCount = logical; ImageCount = distinct;
        BaseHash = Convert.ToHexString(SHA256.HashData(durable));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(number, bytes); hash.AppendData(number[..4]);
        hash.AppendData(durable);
        foreach (var (block, versions) in domains)
        {
            BinaryPrimitives.WriteUInt32BigEndian(number, block);
            BinaryPrimitives.WriteInt32BigEndian(number[4..], versions.Length); hash.AppendData(number);
            foreach (var value in versions) hash.AppendData(value);
        }
        Id = Convert.ToHexString(hash.GetHashAndReset());
    }

    internal bool Covers(CrashStateSpace other)
    {
        if (blockBytes != other.blockBytes || !durable.AsSpan().SequenceEqual(other.durable)) return false;
        foreach (var (block, theirs) in other.domains)
        {
            if (!domains.TryGetValue(block, out var ours)) return false;
            foreach (var value in theirs)
                if (!ours.Any(candidate => candidate.AsSpan().SequenceEqual(value))) return false;
        }
        return true;
    }

    internal byte[] ImageAt(long selection)
    {
        if (selection < 0 || selection >= ImageCount) throw new ArgumentOutOfRangeException(nameof(selection));
        var image = durable.ToArray();
        foreach (var (block, versions) in domains)
        {
            versions[(int)(selection % versions.Length)].CopyTo(image, checked((int)block * blockBytes));
            selection /= versions.Length;
        }
        return image;
    }
}

internal sealed class CrashStateCorpus
{
    internal sealed record Entry(CrashStateSpace Space, string Before, string After);
    private readonly List<Entry> entries = new();
    internal IReadOnlyList<Entry> Entries => entries;
    internal static bool Covers(Entry covering, Entry covered) =>
        covering.Before == covered.Before && covering.After == covered.After && covering.Space.Covers(covered.Space);
    internal void Add(Entry candidate)
    {
        if (entries.Any(entry => Covers(entry, candidate))) return;
        entries.RemoveAll(entry => Covers(candidate, entry));
        entries.Add(candidate);
    }
}
