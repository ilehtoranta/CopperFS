using System.Buffers.Binary;

public sealed class StorageMatrixDefinitionTests
{
    [Theory]
    [InlineData("legacy-zero",40,63,true,false,false)]
    [InlineData("stock-ide-hdd",39,106,false,true,true)]
    [InlineData("stock-ks31-ide-cf",40,68,false,true,true)]
    [InlineData("fast-ks31-ide-hdd",40,68,false,false,true)]
    public void ScenarioPinsRomAndMemoryLayout(string scenario,int version,int revision,bool legacy,bool stock,bool ide)
    {
        var machine=StorageMatrixDefinition.Parse(scenario);
        Assert.Equal(version,machine.RomVersion); Assert.Equal(revision,machine.RomRevision);
        Assert.Equal(legacy,machine.Legacy); Assert.Equal(stock,machine.Stock); Assert.Equal(ide,machine.Ide);
    }

    [Theory]
    [InlineData("legacy-ks31-zero")]
    [InlineData("stock-ks31-ks31-zero")]
    [InlineData("unknown-ide-hdd")]
    public void InvalidScenarioCannotMasqueradeAsAConfiguredMachine(string scenario)
        =>Assert.Throws<ArgumentException>(()=>StorageMatrixDefinition.Parse(scenario));

    [Fact]
    public void Kickstart31RequiresA1200IdentityAsWellAsVersion()
    {
        var rom=new byte[524288];
        BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(12),40); BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(14),63);
        var machine=StorageMatrixDefinition.Parse("stock-ks31-ide-hdd");
        Assert.Throws<InvalidDataException>(()=>StorageMatrixDefinition.ValidateRom(machine,rom)); // A500 revision
        BinaryPrimitives.WriteUInt16BigEndian(rom.AsSpan(14),68);
        Assert.Throws<InvalidDataException>(()=>StorageMatrixDefinition.ValidateRom(machine,rom)); // Wrong-model or patched 40.68
    }

    [Fact]
    public void KnownGeometryErrorsRemainSpecificToTheirRomRevision()
    {
        var old=StorageMatrixDefinition.Parse("stock-ide-cf");
        var current=StorageMatrixDefinition.Parse("stock-ks31-ide-cf");
        Assert.True(StorageMatrixDefinition.KnownGeometryRejection(old,-1));
        Assert.False(StorageMatrixDefinition.KnownGeometryRejection(old,-3));
        Assert.True(StorageMatrixDefinition.KnownGeometryRejection(current,-3));
        Assert.False(StorageMatrixDefinition.KnownGeometryRejection(current,-1));
        Assert.False(StorageMatrixDefinition.KnownGeometryRejection(current,0));
    }

    [Fact]
    public void ProfileSelectionRejectsEmptyDuplicatesAndUnknownNames()
    {
        Assert.Throws<ArgumentException>(()=>StorageMatrixDefinition.Profiles([]));
        Assert.Throws<ArgumentException>(()=>StorageMatrixDefinition.Profiles(["sfs2-csun","sfs2-csun"]));
        Assert.Throws<ArgumentException>(()=>StorageMatrixDefinition.Profiles(["other"]));
    }
}
