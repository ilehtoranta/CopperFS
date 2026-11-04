using CopperStart.Dos;
using System.Reflection;

namespace CopperFS.Tests;

public sealed class StandaloneDependencyTests
{
    [Fact]
    public void HandlerAssemblyGraphDoesNotRequireCopperStart()
    {
        var root = typeof(NativeSfsStandaloneHandler).Assembly;
        Assert.Equal("CopperFS.Amiga", root.GetName().Name);
        var visited = new HashSet<string>();
        var pending = new Queue<Assembly>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var assembly))
        {
            if (!visited.Add(assembly.FullName!)) continue;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                Assert.False(reference.Name!.StartsWith("CopperStart", StringComparison.Ordinal),
                    $"{assembly.GetName().Name} depends on {reference.Name}");
                if (reference.Name.StartsWith("Copper", StringComparison.Ordinal))
                    pending.Enqueue(Assembly.Load(reference));
            }
        }
    }
}
