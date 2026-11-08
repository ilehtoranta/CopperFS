using System.Buffers.Binary;

// Host-only benchmark fixture construction. The manually registered DosEnvec
// describes a real partition after one reserved cylinder; no guest OS patch.
internal static class StorageIdePartition
{
    internal const string Layout="partitioned-v1";
    internal const uint SectorsPerCylinder=256;
    internal const uint FirstSector=SectorsPerCylinder;
    internal const int FirstByte=(int)FirstSector*512;

    internal static byte[] Create(ReadOnlySpan<byte> filesystem,bool sfs2)
    {
        if(filesystem.Length<FirstByte || filesystem.Length%FirstByte!=0)
            throw new ArgumentException("IDE partition fixtures must contain whole 128 KiB cylinders.");
        var drive=new byte[checked(FirstByte+filesystem.Length)];
        filesystem.CopyTo(drive.AsSpan(FirstByte));
        if(sfs2)
        {
            foreach(var offset in new[]{FirstByte,drive.Length-512})
            {
                var root=drive.AsSpan(offset,512);
                if(BinaryPrimitives.ReadUInt32BigEndian(root)!=0x53465302 || BinaryPrimitives.ReadUInt16BigEndian(root[12..])!=4)
                    throw new InvalidDataException("Expected primary and backup SFS2 root blocks.");
                // SFS root byte ranges are absolute device positions. Block
                // numbers, extent keys and payload stay partition-relative.
                BinaryPrimitives.WriteUInt64BigEndian(root[32..],FirstByte);
                BinaryPrimitives.WriteUInt64BigEndian(root[40..],(ulong)drive.Length);
                BinaryPrimitives.WriteUInt32BigEndian(root[4..],0);
                uint sum=2; // SFS2 checksum bias, including the SFS\\2 identifier.
                for(var at=0;at<512;at+=4) sum=unchecked(sum+BinaryPrimitives.ReadUInt32BigEndian(root[at..]));
                BinaryPrimitives.WriteUInt32BigEndian(root[4..],unchecked(0u-sum));
            }
        }
        return drive;
    }
}
