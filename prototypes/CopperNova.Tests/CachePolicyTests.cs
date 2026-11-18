using System.Text.Json;
using CopperNova.CacheBench;
using static CopperNova.Tests.FilesystemTests;

namespace CopperNova.Tests;

public class CachePolicyTests
{
    [Fact] public void ReusedMetadataSurvivesAWorkingSetLargerThanTheCache()
    {
        var scenario=new CachePressureScenario(); scenario.Seed();
        var result=JsonSerializer.SerializeToElement(scenario.Measure());
        Assert.True(result.GetProperty("Passed").GetBoolean());
        Assert.InRange(result.GetProperty("MetadataSectorReads").GetInt32(),1,350);
        Assert.Equal(1512,result.GetProperty("DataSectorReads").GetInt32());
        Assert.Equal(0,result.GetProperty("WriteRequests").GetInt32());
        Assert.Equal(0,result.GetProperty("Flushes").GetInt32());
    }
    [Fact] public void FlushRetainsValidatedMetadataAndDoesNotEvictIt()
    {
        var h=new Harness(); h.Mount(); uint file=h.Open("retained"); h.Write(file,new byte[512]); h.Close(file); h.Flush();
        int metadataReads=0;
        h.BeforeIo=io=> { if(io.Kind==CopperCPX.IoKind.Read&&io.Sector.Low<1280) metadataReads++; };
        for(int i=0;i<5;i++) { file=h.Open("retained",1005); h.Close(file); h.Flush(); }
        Assert.Equal(0,metadataReads);
    }
}
