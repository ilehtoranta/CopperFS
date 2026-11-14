using System.Text;
using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class LinkPacketTests
{
    private static uint Cstr(DispatcherDriver d,string s,uint at=90000)
    { var bytes=Encoding.Latin1.GetBytes(s); bytes.CopyTo(d.Ram.Bytes,(int)at); d.Ram.Write8(at+(uint)bytes.Length,0); return at; }
    private static void Soft(DispatcherDriver d,string name,string target)
        =>Assert.Equal((uint.MaxValue,0u),d.Packet(1021,0,d.Bstr(name),Cstr(d,target),1));
    private static void Qualified(DispatcherDriver d,uint directory,string path,string expected)
    {
        uint input=Cstr(d,path),size=(uint)expected.Length+1;
        Assert.Equal((size,0u),d.Packet(26406,directory,input,0,uint.MaxValue));
        // The destination begins in the source CString and can be longer than it.
        uint guard=input+Math.Max(size+1,(uint)path.Length+1); d.Ram.Write8(guard,0xa5);
        Assert.Equal((size-1,0u),d.Packet(26406,directory,input,input+1,size));
        Assert.Equal(Encoding.Latin1.GetBytes(expected+"\0"),d.Ram.Bytes.AsSpan((int)input+1,(int)size).ToArray());
        Assert.Equal(0xa5,d.Ram.Read8(guard));
    }
    [Fact]
    public void NewReadLinkQualifiesFromDirectoryLocksAndThePersistedLabel()
    {
        using var d=new DispatcherDriver(512);
        Assert.Equal((uint.MaxValue,0u),d.Packet(9,d.Bstr("Données")));
        string parent=new('é',88);
        uint dir=d.Packet(22,0,d.Bstr(parent)).Result; Assert.NotEqual(0u,dir); d.Packet(15,dir);
        dir=d.Packet(22,0,d.Bstr(parent+"/Child")).Result; d.Packet(15,dir);
        Soft(d,parent+"/Child/link","café/"); Soft(d,parent+"/volume",":root"); Soft(d,"dev","RAM:");
        dir=d.Packet(8,0,d.Bstr(parent+"/Child"),0xfffffffe).Result;
        Qualified(d,dir,"link//tail","Données:"+parent+"/Child/café//tail");
        Qualified(d,dir,"/volume/a","Données:root/a");
        Qualified(d,dir,"anything:dev/a","RAM:a");
        d.Packet(15,dir); byte[] before=(byte[])d.Image.Clone();
        Qualified(d,0,"/dev/"+new string('x',1018),"RAM:"+new string('x',1018));
        Assert.Equal(before,d.Image); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void NewReadLinkCanonicalHardNameTracksRenamesAndUnlinksAcrossDirectories()
    {
        using var d=new DispatcherDriver(512); uint dir=d.Packet(22,0,d.Bstr("Dir")).Result; d.Packet(15,dir);
        d.Execute(new(ActionKind.Create,"Dir/original"));
        d.Execute(new(ActionKind.HardLink,"Dir/original","alias"));
        d.Execute(new(ActionKind.HardLink,"alias","third"));
        Qualified(d,0,"alias/suffix","CopperCPX4:Dir/original/suffix");
        Assert.Equal((uint.MaxValue,212u),d.Packet(26406,0,Cstr(d,"Dir/original")));
        d.Execute(new(ActionKind.Rename,"Dir/original","Dir/moved"));
        Qualified(d,0,"third","CopperCPX4:Dir/moved");
        d.Execute(new(ActionKind.Delete,"Dir/moved"));
        Qualified(d,0,"third","CopperCPX4:alias");
        Assert.Equal((uint.MaxValue,212u),d.Packet(26406,0,Cstr(d,"alias")));
        d.Execute(new(ActionKind.Delete,"alias"));
        Assert.Equal((uint.MaxValue,212u),d.Packet(26406,0,Cstr(d,"third")));
        new IndependentImage(d.Image); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void NewReadLinkRejectsShortBuffersBadLocksAndOfflineMedia()
    {
        using var d=new DispatcherDriver(512); Soft(d,"link","link"); uint input=Cstr(d,"link");
        d.Ram.Bytes.AsSpan(92000,16).Fill(0xa5);
        foreach(uint capacity in new uint[]{0,15,uint.MaxValue})
            Assert.Equal((0xfffffffeu,120u),d.Packet(26406,0,input,92000,capacity));
        Assert.Equal((uint.MaxValue,115u),d.Packet(26406,0,input,uint.MaxValue-3,16));
        Assert.Equal((uint.MaxValue,211u),d.Packet(26406,uint.MaxValue,input,92000,16));
        Assert.Equal((uint.MaxValue,115u),d.Packet(26406,0,0,92000,16));
        Assert.Equal((uint.MaxValue,205u),d.Packet(26406,0,Cstr(d,"missing"),92000,16));
        Assert.Equal((uint.MaxValue,120u),d.Packet(26406,0,Cstr(d,new string('x',1024)),92000,16));
        Assert.Equal((uint.MaxValue,210u),d.Packet(26406,0,Cstr(d,new string('x',256)),92000,16));
        Qualified(d,0,"link","CopperCPX4:link");
        Assert.Equal((uint.MaxValue,0u),d.Packet(31,1));
        Assert.Equal((uint.MaxValue,226u),d.Packet(26406,0,input,92000,16));
        Assert.All(d.Ram.Bytes[92000..92016],b=>Assert.Equal(0xa5,b));
    }
    [Fact]
    public void NewReadLinkFits4095BytesAndRefusesTheNextByte()
    {
        using var d=new DispatcherDriver(512); string component=new('n',100); uint parent=0;
        for(int n=0;n<32;n++)
        {
            var child=d.Packet(22,parent,d.Bstr(component)); Assert.Equal(0u,child.Error);
            if(parent!=0) Assert.Equal((uint.MaxValue,0u),d.Packet(15,parent));
            parent=child.Result;
        }
        string target=new('t',768),suffix="/"+new string('s',83);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1021,parent,d.Bstr("link"),Cstr(d,target),1));
        string expected="CopperCPX4:"+string.Concat(Enumerable.Repeat(component+"/",32))+target+suffix;
        Assert.Equal(4095,expected.Length); Qualified(d,parent,"link"+suffix,expected);
        uint input=Cstr(d,"link"+suffix+"s"); d.Ram.Write8(95000,0xa5);
        Assert.Equal((0xfffffffeu,120u),d.Packet(26406,parent,input,0,0));
        Assert.Equal((0xfffffffeu,120u),d.Packet(26406,parent,input,95000,4097));
        Assert.Equal(0xa5,d.Ram.Read8(95000)); d.Packet(15,parent); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void NewReadLinkKeepsCallerBufferUntouchedOnFailedOrCancelledIo(bool removed)
    {
        using var d=new DispatcherDriver(512); Soft(d,"soft","target"); byte[] before=(byte[])d.Image.Clone();
        d.Ram.Bytes.AsSpan(92000,64).Fill(0xa5);
        InhibitLifecycleTests.Begin(d,26406,0,Cstr(d,"soft"),92000);
        var io=InhibitLifecycleTests.NextIo(d); Assert.Equal(IoKind.Read,io.Kind);
        if(removed) d.Dispatcher.MediaRemoved();
        Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); Assert.Equal(io,d.Dispatcher.Request);
        Assert.Equal(CpxError.Io,d.Dispatcher.CompleteIo(io.Token,0,false));
        Assert.Equal((uint.MaxValue,removed?226u:213u),InhibitLifecycleTests.Finish(d));
        Assert.All(d.Ram.Bytes[92000..92064],b=>Assert.Equal(0xa5,b));
        Assert.Equal(before,d.Image); Assert.False(d.Dispatcher.HasOpenResources);
    }
    private static string Read(DispatcherDriver d,string path,uint lockBptr=0)
    {
        var r=d.Packet(1024,lockBptr,Cstr(d,path),92000,2048); Assert.Equal(0u,r.Error);
        Assert.InRange(r.Result,1u,2047u); Assert.Equal(0,d.Ram.Read8(92000+r.Result));
        return Encoding.Latin1.GetString(d.Ram.Bytes,92000,(int)r.Result);
    }
    [Theory][InlineData("target","dir/target/tail")][InlineData(":target","CopperCPX4:target/tail")]
    [InlineData("RAM:target","RAM:target/tail")][InlineData("target/","dir/target/tail")][InlineData("RAM:","RAM:tail")]
    public void ReadLinkMergesRelativeAbsoluteAndIntermediateComponents(string target,string expected)
    {
        using var d=new DispatcherDriver(512); uint dir=d.Packet(22,0,d.Bstr("dir")).Result; d.Packet(15,dir);
        Soft(d,"dir/link",target); Assert.Equal(expected,Read(d,"dir/link/tail"));
        Assert.Equal((0u,233u),d.Packet(1005,DispatcherDriver.FileHandle>>2,0,d.Bstr("dir/link/tail")));
        Assert.Equal((0u,233u),d.Packet(8,0,d.Bstr("dir/link"),0xfffffffe));
        new IndependentImage(d.Image); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact]
    public void LongLatin1LinkAndExactOutputCapacityAreCheckedBeforePublishing()
    {
        using var d=new DispatcherDriver(512); string target=new('é',384); Soft(d,"link",target); Assert.Equal(target,Read(d,"link"));
        d.Ram.Bytes.AsSpan(92000,385).Fill(0xa5);
        Assert.Equal((0xfffffffeu,120u),d.Packet(1024,0,Cstr(d,"link"),92000,384)); Assert.All(d.Ram.Bytes[92000..92385],b=>Assert.Equal(0xa5,b));
        Assert.Equal((384u,0u),d.Packet(1024,0,Cstr(d,"link"),92000,385)); Assert.Equal(0,d.Ram.Read8(92384));
        byte[] before=(byte[])d.Image.Clone();
        Assert.Equal((0u,115u),d.Packet(1021,0,d.Bstr("too-long"),Cstr(d,new string('é',385)),1)); Assert.Equal(before,d.Image);
        Assert.Equal((uint.MaxValue,205u),d.Packet(1024,0,Cstr(d,"missing"),92000,385));
        Assert.Equal((uint.MaxValue,115u),d.Packet(1024,0,Cstr(d,"link"),uint.MaxValue-2,10));
    }
    [Fact]
    public void HardLinksPreserveContentsAndEntrySpecificParentsThroughPackets()
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file")); d.Execute(new(ActionKind.Write,"file",Data:[3,1,4]));
        uint locked=d.Packet(8,0,d.Bstr("file"),0xfffffffe).Result;
        Assert.Equal((uint.MaxValue,0u),d.Packet(1021,0,d.Bstr("linked"),locked,0));
        Assert.Equal((0u,203u),d.Packet(1021,0,d.Bstr("linked"),locked,0));
        Assert.Equal((uint.MaxValue,0u),d.Packet(15,locked)); d.Execute(new(ActionKind.Delete,"file"));
        Assert.Equal(new byte[]{3,1,4},d.Execute(new(ActionKind.Read,"linked",Data:new byte[3])).Data);
        Assert.Equal((uint.MaxValue,205u),d.Packet(1024,0,Cstr(d,"linked"),92000,2048)); new IndependentImage(d.Image);
    }
    [Fact]
    public void ParentComponentsResolveFromDirectoryOrFileAndStopAtRoot()
    {
        using var d=new DispatcherDriver(512); uint dir=d.Packet(22,0,d.Bstr("dir")).Result; d.Packet(15,dir);
        d.Execute(new(ActionKind.Create,"dir/file")); d.Execute(new(ActionKind.Create,"sibling")); Soft(d,"link","sibling");
        uint locked=d.Packet(8,0,d.Bstr("dir/file"),0xfffffffe).Result;
        Assert.Equal("//sibling",Read(d,"//link",locked));
        var root=d.Packet(8,locked,d.Bstr("//"),0xfffffffe); Assert.Equal(0u,root.Error); Assert.NotEqual(0u,root.Result);
        Assert.Equal((0u,205u),d.Packet(8,0,d.Bstr("/"),0xfffffffe)); d.Packet(15,root.Result); d.Packet(15,locked);
    }
    [Fact]
    public void CyclicLinksReturnOneExpansionPerPacketWithoutLoopingOrOwningResources()
    {
        using var d=new DispatcherDriver(512); Soft(d,"a","b"); Soft(d,"b","a"); byte[] before=(byte[])d.Image.Clone();
        for(int n=0;n<20;n++) { Assert.Equal(n%2==0?"b":"a",Read(d,n%2==0?"a":"b")); Assert.False(d.Dispatcher.HasOpenResources); }
        Assert.Equal(before,d.Image);
    }
}
