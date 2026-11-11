// Reduced from the Header/Put/WriteOfs shape in CopperFS revision
// c26e6e1c73b93c2bce3152b326f4dbedf559a946, MIT licensed.
using System.Runtime.CompilerServices;

namespace InlineSize;

public interface IDevice
{
    int Length { get; }
    int Read(int index);
    void Write(int index, int value);
    bool Commit(int id);
}

public struct Device : IDevice
{
    private readonly int[] words;
    public Device(int[] words) => this.words = words;
    public int Length => words.Length;
    [MethodImpl(MethodImplOptions.NoInlining)] public int Read(int index) => words[index];
    [MethodImpl(MethodImplOptions.NoInlining)] public void Write(int index, int value) => words[index] = value;
    [MethodImpl(MethodImplOptions.NoInlining)] public bool Commit(int id) => id >= 0;
}

public struct State { public int Error; public int Header; }

public static class Filesystem
{
    private static bool Fail(ref State state) { state.Error = 1; return false; }

    private static bool Header<T>(ref T device, ref State state, int id) where T : struct, IDevice
    {
        if (id < 0 || device.Read(0) != id) return Fail(ref state);
        return true;
    }

    private static bool Put<T>(ref T device, ref State state, int id, int checksum) where T : struct, IDevice
    {
        if (state.Error != 0) return false;
        if (checksum >= 0)
        {
            device.Write(checksum, 0);
            var sum = 0;
            for (var i = 0; i < device.Length; i++) sum = unchecked(sum + device.Read(i));
            device.Write(checksum, unchecked(-sum));
        }
        return device.Commit(id) || Fail(ref state);
    }

    // Keep the caller as a separate JIT-disassembly target; CILica may still
    // inline calls inside it. Its shape follows the repeated calls in WriteOfs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Write<T>(ref T device, ref State state, int id) where T : struct, IDevice
    {
        if (!Header(ref device, ref state, id)) return false;
        if (!Put(ref device, ref state, id, 1)) return false;
        if (!Put(ref device, ref state, id, 2)) return false;
        if (!Header(ref device, ref state, id)) return false;
        if (!Put(ref device, ref state, id, 3)) return false;
        if (!Put(ref device, ref state, id, 4)) return false;
        if (!Header(ref device, ref state, id)) return false;
        if (!Put(ref device, ref state, id, 5)) return false;
        return Put(ref device, ref state, id, 6);
    }
}
