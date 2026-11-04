namespace CopperSFS.Tests;

public sealed class CrashStateSpaceTests
{
    private static KeyValuePair<uint, List<byte[]>> Pending(uint block, params byte[] values) =>
        new(block, values.Select(v => new[] { v }).ToList());
    [Fact]
    public void EnumeratesWholeProductAndKeepsIntermediateVersions()
    {
        var space = new CrashStateSpace([0, 0], 1, [Pending(0, 1, 2), Pending(1, 3)]);
        Assert.Equal(6, space.ImageCount); Assert.Equal(6, space.LogicalCount);
        Assert.Equal(new[] { "0000", "0003", "0100", "0103", "0200", "0203" },
            Enumerable.Range(0, 6).Select(i => Convert.ToHexString(space.ImageAt(i))).Order());
    }
    [Fact]
    public void EqualLatestImagesDoNotHideDifferentWriteHistories()
    {
        var complete = new CrashStateSpace([0], 1, [Pending(0, 1, 2)]);
        var latestOnly = new CrashStateSpace([0], 1, [Pending(0, 2)]);
        Assert.True(complete.Covers(latestOnly)); Assert.False(latestOnly.Covers(complete));
    }
    [Fact]
    public void EveryByteOfTheDurableImageMustMatchForContainment()
    {
        var first = new CrashStateSpace([0, 0], 1, [Pending(0, 1, 2)]);
        var other = new CrashStateSpace([0, 3], 1, [Pending(0, 1, 2)]);
        Assert.False(first.Covers(other)); Assert.False(other.Covers(first));
    }
    [Fact]
    public void NormalizingDuplicateVersionsPreservesExactImageSet()
    {
        var image = new byte[] { 0, 0 }; List<byte[]> versions = [[1], [0], [1]];
        var space = new CrashStateSpace(image, 1, [new(0, versions)]);
        image[0] = 9; versions[0][0] = 8;
        Assert.Equal(4, space.LogicalCount); Assert.Equal(2, space.ImageCount);
        Assert.Equal("0000", Convert.ToHexString(space.ImageAt(0)));
        Assert.Equal("0100", Convert.ToHexString(space.ImageAt(1)));
    }
    [Fact]
    public void CompressionPreservesTheUnionOfAllInputSpaces()
    {
        var corpus = new CrashStateCorpus(); var expected = new HashSet<string>();
        var random = new Random(104729);
        for (var i = 0; i < 100; i++)
        {
            var pending = Enumerable.Range(0, 4).Select(block => Pending((uint)block,
                Enumerable.Range(0, random.Next(1, 5)).Select(_ => (byte)random.Next(4)).ToArray()));
            var space = new CrashStateSpace(new byte[4], 1, pending);
            for (long state = 0; state < space.ImageCount; state++) expected.Add(Convert.ToHexString(space.ImageAt(state)));
            corpus.Add(new(space, "old", "new"));
        }
        var actual = new HashSet<string>();
        foreach (var entry in corpus.Entries)
            for (long state = 0; state < entry.Space.ImageCount; state++) actual.Add(Convert.ToHexString(entry.Space.ImageAt(state)));
        Assert.True(expected.SetEquals(actual));
    }
    [Fact]
    public void DifferentExpectedResultsAreKeptSeparate()
    {
        var space = new CrashStateSpace([0], 1, [Pending(0, 1)]);
        var corpus = new CrashStateCorpus();
        corpus.Add(new(space, "old", "new")); corpus.Add(new(space, "new", "new"));
        Assert.Equal(2, corpus.Entries.Count);
    }
}
