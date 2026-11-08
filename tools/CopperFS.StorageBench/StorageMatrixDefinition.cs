using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO.Compression;

// Shared by the host runner and ROM harness; no filesystem or emulator dependency.
internal static class StorageMatrixDefinition
{
    internal static readonly string[] DefaultProfiles=["ffs","sfs2-csun","sfs2-trst"];
    internal static readonly string[] DefaultScenarios=["legacy-zero","stock-zero","stock-hdd","stock-cf","stock-ide-hdd","stock-ide-cf",
        "fast-zero","fast-hdd","fast-cf","fast-ide-hdd","fast-ide-cf"];
    // Identification metadata, not ROM data: WinUAE rommgr.cpp, entry 15.
    // https://github.com/tonioni/WinUAE/blob/master/rommgr.cpp
    internal const string A1200Kickstart31Sha1="E21545723FE8374E91342617604F1B3D703094F1";

    internal readonly record struct Machine(bool Legacy,bool Stock,bool Ide,bool Kickstart31,int RomVersion,int RomRevision);
    internal static Machine Parse(string scenario)
    {
        var ks31=scenario.Contains("-ks31-",StringComparison.Ordinal);
        var baseScenario=ks31 ? scenario.Replace("-ks31-","-",StringComparison.Ordinal) : scenario;
        if(!DefaultScenarios.Contains(baseScenario) || ks31 && baseScenario=="legacy-zero")
            throw new ArgumentException($"Unknown storage scenario: {scenario}");
        var legacy=scenario=="legacy-zero";
        return new(legacy,scenario.StartsWith("stock-",StringComparison.Ordinal),scenario.Contains("-ide-",StringComparison.Ordinal),
            ks31,legacy || ks31 ? 40 : 39,legacy ? 63 : ks31 ? 68 : 106);
    }

    internal static string[] Profiles(string[]? selected)
    {
        var profiles=selected ?? DefaultProfiles;
        if(profiles.Length==0 || profiles.Distinct().Count()!=profiles.Length || profiles.Any(p=>!DefaultProfiles.Contains(p)))
            throw new ArgumentException("Profiles must select distinct ffs, sfs2-csun and/or sfs2-trst entries.");
        return profiles;
    }

    internal static void ValidateRom(Machine machine,ReadOnlySpan<byte> rom)
    {
        if(rom.Length!=524288 || BinaryPrimitives.ReadUInt16BigEndian(rom[12..])!=machine.RomVersion ||
            BinaryPrimitives.ReadUInt16BigEndian(rom[14..])!=machine.RomRevision)
            throw new InvalidDataException($"Expected a 512 KiB Kickstart {machine.RomVersion}.{machine.RomRevision} ROM.");
        // A3000/A4000 also have revision 40.68. Pin the unmodified A1200 image,
        // independently of filenames, so an unrelated ROM cannot supply a baseline.
        if(machine.Kickstart31 && Convert.ToHexString(SHA1.HashData(rom))!=A1200Kickstart31Sha1)
            throw new InvalidDataException("Expected the unmodified A1200 Kickstart 3.1 (40.68) ROM; its model hash differs.");
    }

    internal static bool KnownGeometryRejection(Machine machine,int error)=>
        machine.Kickstart31 ? error==-3 : !machine.Legacy && error==-1;

    internal static byte[] LoadRom(string path)
    {
        const int maximumImage=524288+11;
        byte[] image;
        if(Path.GetExtension(path).Equals(".zip",StringComparison.OrdinalIgnoreCase))
        {
            using var zip=ZipFile.OpenRead(path);
            var entry=zip.Entries.Single(e=>e.FullName.EndsWith(".rom",StringComparison.OrdinalIgnoreCase));
            if(entry.Length>maximumImage) throw new InvalidDataException("Benchmark ROM image is too large.");
            using var input=entry.Open(); using var buffer=new MemoryStream(); input.CopyTo(buffer); image=buffer.ToArray();
        }
        else
        {
            if(new FileInfo(path).Length>maximumImage) throw new InvalidDataException("Benchmark ROM image is too large.");
            image=File.ReadAllBytes(path);
        }
        if(!image.AsSpan().StartsWith("AMIROMTYPE1"u8)) return image;
        // Same licensed-file convention as CopperScreenKickstartRomArchive.
        // Read the user's key locally; never copy it into benchmark artifacts.
        var keyPath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"rom.key");
        if(!File.Exists(keyPath)) throw new InvalidDataException("The encoded ROM requires its rom.key in the same folder.");
        if(new FileInfo(keyPath).Length is <=0 or >524288) throw new InvalidDataException("The local ROM key has an invalid length.");
        var key=File.ReadAllBytes(keyPath);
        try
        {
            var decoded=image.AsSpan(11).ToArray();
            for(var i=0;i<decoded.Length;i++) decoded[i]^=key[i%key.Length];
            return decoded;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
