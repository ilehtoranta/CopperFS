using InlineSize;
using System.Reflection;
using System.Runtime.InteropServices;

Console.WriteLine($"RUNTIME {RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}");
foreach (var methodName in new[] { "Write", "Put", "Header" })
{
    var method = typeof(Filesystem).GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    Console.WriteLine($"IL {methodName} {method.GetMethodBody()!.GetILAsByteArray()!.Length}");
}

Check("valid", 42, 0, true, 0, new[] { 42, -69, 2, 3, 4, 5, 6, 7 });
Check("wrong-header", 41, 0, false, 1, new[] { 42, 1, 2, 3, 4, 5, 6, 7 });
Check("negative-id", -1, 0, false, 1, new[] { 42, 1, 2, 3, 4, 5, 6, 7 });
Check("prior-error", 42, 7, false, 7, new[] { 42, 1, 2, 3, 4, 5, 6, 7 });

static void Check(string name, int id, int error, bool expected, int expectedError, int[] expectedWords)
{
    var values = new[] { 42, 1, 2, 3, 4, 5, 6, 7 };
    var device = new Device(values);
    var state = new State { Error = error };
    bool actual = Filesystem.Write(ref device, ref state, id);
    if (actual != expected || state.Error != expectedError || !values.SequenceEqual(expectedWords))
        throw new InvalidOperationException($"Wrong result for {name}");
    Console.WriteLine($"RESULT {name} {actual} {state.Error} {string.Join(',', values)}");
}
