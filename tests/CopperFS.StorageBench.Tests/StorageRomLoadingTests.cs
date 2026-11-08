using System.IO.Compression;

public sealed class StorageRomLoadingTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"copper-rom-loader-"+Guid.NewGuid().ToString("N"));
    public StorageRomLoadingTests()=>Directory.CreateDirectory(root);
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RawAndZipPreserveRomBytes(bool zip)
    {
        byte[] expected=[0x11,0x22,0x33,0x44]; var path=Path.Combine(root,zip ? "rom.zip" : "rom.rom");
        if(zip) { using var archive=ZipFile.Open(path,ZipArchiveMode.Create); using var output=archive.CreateEntry("rom.rom").Open(); output.Write(expected); }
        else File.WriteAllBytes(path,expected);
        Assert.Equal(expected,StorageMatrixDefinition.LoadRom(path));
    }
    [Fact]
    public void LicensedEncodingUsesTheAdjacentKeyWithoutModifyingEitherInput()
    {
        byte[] key=[0x12,0x34,0x56]; byte[] expected=[1,2,3,4,5,6,7];
        var encoded="AMIROMTYPE1"u8.ToArray().Concat(expected.Select((b,i)=>(byte)(b^key[i%key.Length]))).ToArray();
        var path=Path.Combine(root,"encoded.rom"); var keyPath=Path.Combine(root,"rom.key");
        File.WriteAllBytes(path,encoded); File.WriteAllBytes(keyPath,key);
        Assert.Equal(expected,StorageMatrixDefinition.LoadRom(path));
        Assert.Equal(encoded,File.ReadAllBytes(path)); Assert.Equal(key,File.ReadAllBytes(keyPath));
    }
    [Fact]
    public void MissingOrEmptyKeyIsUnavailableInput()
    {
        var path=Path.Combine(root,"encoded.rom"); File.WriteAllBytes(path,"AMIROMTYPE1"u8.ToArray().Concat(new byte[32]).ToArray());
        Assert.Throws<InvalidDataException>(()=>StorageMatrixDefinition.LoadRom(path));
        File.WriteAllBytes(Path.Combine(root,"rom.key"),[]);
        Assert.Throws<InvalidDataException>(()=>StorageMatrixDefinition.LoadRom(path));
    }
    public void Dispose()
    {
        var full=Path.GetFullPath(root);
        if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test cleanup path.");
        Directory.Delete(full,true);
    }
}
