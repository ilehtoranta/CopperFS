using Amiga;

namespace CopperSFS;

/// <summary>Optional caller-owned four-byte validation certificate. The mount
/// owner MUST clear it before any write (including recovery and failed writes),
/// media/geometry change or external access. It certifies only global allocation
/// preflight; each window still recovers, flushes and checks current bitmap pages.
/// Null selects uncached validation. Never share storage with other scratch.</summary>
public static class AmigaTrimValidation
{
    public static bool Current<T>(ref T p, APTR certificate) where T : struct, IAmigaGuestMemory =>
        certificate.IsNotNull && p.ReadUInt32(certificate) == 1;

    public static void Complete<T>(ref T p, APTR certificate) where T : struct, IAmigaGuestMemory
    {
        if (certificate.IsNotNull) p.WriteUInt32(certificate, 0, 1);
    }
}
