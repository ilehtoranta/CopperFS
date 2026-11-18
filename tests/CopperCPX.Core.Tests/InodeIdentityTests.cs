namespace CopperCPX.Tests;

public class InodeIdentityTests
{
    private static CpxInodeIdentity Codec(uint sectors = 8192)
    {
        Assert.Equal(CpxError.None, CpxShadowMapGeometry.TryCreate(512, 512, Wide.From(sectors), Wide.From(512), out var geometry));
        Assert.Equal(CpxError.None, CpxInodeIdentity.TryCreate(geometry, Wide.From(16), out var codec));
        return codec;
    }

    [Fact]
    public void ReusingASlotKeepsMonotonicIdsAndNeverMatchesTheOldOwner()
    {
        var codec = Codec();
        Assert.Equal(CpxError.None, codec.TryAssign(Wide.From(2), Wide.From(32), out var first, out var next));
        Assert.Equal(16UL, (ulong)first); Assert.Equal(17UL, (ulong)next);
        Assert.Equal(CpxError.None, codec.TryAssign(next, Wide.From(34), out var second, out next)); Assert.Equal(17UL, (ulong)second);
        Assert.Equal(CpxError.None, codec.TryAssign(next, Wide.From(32), out var reused, out next)); Assert.Equal(4112UL, (ulong)reused);
        Assert.True(codec.TryPair(first, out var oldPair)); Assert.True(codec.TryPair(reused, out var newPair));
        Assert.Equal(oldPair, newPair); Assert.False(codec.Matches(first, reused, newPair)); Assert.True(codec.Matches(reused, reused, newPair));
        Assert.Equal(CpxError.None, codec.TryAssign(next, Wide.From(36), out var later, out next)); Assert.Equal(4114UL, (ulong)later);
    }

    [Theory][InlineData(8192u)][InlineData(9000u)]
    public void RootIdentityAndUnusedSlotsCannotBecomeOtherOwners(uint sectors)
    {
        var codec = Codec(sectors);
        Assert.True(codec.TryPair(Wide.From(1), out var root)); Assert.Equal(16UL, (ulong)root);
        Assert.False(codec.TryPair(default, out _)); Assert.False(codec.TryPair(Wide.From(8), out _));
        Assert.Equal(CpxError.InvalidArgument, codec.TryAssign(Wide.From(2), root, out _, out _));
        if (sectors == 9000) Assert.False(codec.TryPair(Wide.From(8191), out _));
    }

    [Fact]
    public void ExhaustionRejectsBeforeReturningAnUnrepresentableNextCounter()
    {
        var codec = Codec();
        Assert.Equal(CpxError.ResourceExhausted, codec.TryAssign((Wide)ulong.MaxValue, Wide.From(32), out var id, out var next));
        Assert.True(id.IsZero); Assert.True(next.IsZero);
        Assert.Equal(CpxError.InvalidArgument, codec.TryAssign(Wide.From(1), Wide.From(32), out _, out _));
    }

    [Fact]
    public void WidePhysicalSlotsRoundTripWithoutTruncation()
    {
        Assert.Equal(CpxError.None, CpxShadowMapGeometry.TryCreate(512, 512, (Wide)(1UL << 35), Wide.From(1024), out var geometry));
        Assert.Equal(CpxError.None, CpxInodeIdentity.TryCreate(geometry, Wide.From(16), out var codec));
        Wide pair = (Wide)((1UL << 33) + 64);
        Assert.Equal(CpxError.None, codec.TryAssign(Wide.From(2), pair, out var id, out _));
        Assert.True(codec.TryPair(id, out var decoded)); Assert.Equal(pair, decoded); Assert.True(codec.Matches(id, id, decoded));
    }
}
