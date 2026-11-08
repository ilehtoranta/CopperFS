using System.Buffers.Binary;

public sealed class StorageIdePartitionTests
{
    [Fact]
    public void FfsPartitionPreservesTheEntireFilesystemAndReservesOneCylinder()
    {
        var filesystem=Enumerable.Range(0,StorageIdePartition.FirstByte).Select(i=>(byte)(i*31)).ToArray();
        var drive=StorageIdePartition.Create(filesystem,false);
        Assert.Equal(filesystem.Length+131072,drive.Length);
        Assert.True(drive.AsSpan(0,131072).IndexOfAnyExcept((byte)0)<0);
        Assert.Equal(filesystem,drive.AsSpan(131072).ToArray());
    }

    [Fact]
    public void Sfs2BothRootRangesFollowThePartitionAndChecksumsRemainValid()
    {
        var filesystem=new byte[StorageIdePartition.FirstByte];
        foreach(var at in new[]{0,filesystem.Length-512})
        {
            var root=filesystem.AsSpan(at,512);
            BinaryPrimitives.WriteUInt32BigEndian(root,0x53465302);
            BinaryPrimitives.WriteUInt16BigEndian(root[12..],4);
            BinaryPrimitives.WriteUInt32BigEndian(root[8..],(uint)at/512);
        }
        filesystem[8192]=0xA7; var original=filesystem.ToArray();
        var drive=StorageIdePartition.Create(filesystem,true);
        Assert.Equal(original,filesystem);
        Assert.Equal(0xA7,drive[131072+8192]);
        foreach(var at in new[]{131072,drive.Length-512})
        {
            var root=drive.AsSpan(at,512);
            Assert.Equal(131072ul,BinaryPrimitives.ReadUInt64BigEndian(root[32..]));
            Assert.Equal((ulong)drive.Length,BinaryPrimitives.ReadUInt64BigEndian(root[40..]));
            Assert.Equal((uint)(at-131072)/512,BinaryPrimitives.ReadUInt32BigEndian(root[8..]));
            uint sum=0; for(var word=0;word<512;word+=4) sum=unchecked(sum+BinaryPrimitives.ReadUInt32BigEndian(root[word..]));
            Assert.Equal(0xFFFFFFFEu,sum);
        }
    }

    [Fact]
    public void MisalignedOrIncorrectlyTypedFixturesCannotBecomeIdePartitions()
    {
        Assert.Throws<ArgumentException>(()=>StorageIdePartition.Create(new byte[512],false));
        Assert.Throws<InvalidDataException>(()=>StorageIdePartition.Create(new byte[131072],true));
    }
}
