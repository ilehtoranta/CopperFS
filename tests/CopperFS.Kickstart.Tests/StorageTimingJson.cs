using System.Text.Json;
using System.Text.Json.Nodes;
using CopperMod.Amiga.Lightweight;

namespace CopperFS.Kickstart.Tests;

internal static class StorageTimingJson
{
    internal static LightweightDriveTimingProfile? Override(JsonElement root,string name,LightweightDriveTimingProfile preset)
    {
        if(!root.TryGetProperty(name,out var element) || element.ValueKind==JsonValueKind.Null) return null;
        var merged=JsonSerializer.SerializeToNode(preset)!.AsObject();
        foreach(var property in element.EnumerateObject())
        {
            if(!merged.ContainsKey(property.Name)) throw new InvalidDataException($"Unknown timing field {name}.{property.Name}");
            merged[property.Name]=JsonNode.Parse(property.Value.GetRawText());
        }
        return merged.Deserialize<LightweightDriveTimingProfile>()!;
    }
}

public sealed class StorageTimingJsonTests
{
    [Fact]
    public void PartialCfOverridesRetainTheCfPreset()
    {
        using var json=JsonDocument.Parse("{\"Cf\":{\"CommandMicroseconds\":17}}");
        var profile=StorageTimingJson.Override(json.RootElement,"Cf",LightweightDriveTimingProfile.Cf)!;
        Assert.Equal(17,profile.CommandMicroseconds); Assert.Equal(0,profile.RevolutionsPerMinute);
        Assert.Equal(0,profile.ReadAheadBytes); Assert.Equal(8*1024*1024,profile.TransferBytesPerSecond);
    }
}
