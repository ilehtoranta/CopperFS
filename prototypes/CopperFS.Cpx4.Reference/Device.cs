namespace CopperFS.Cpx4.Reference;

/// <summary>Synchronous whole-sector I/O. Flush persists and orders preceding writes.
/// Failure must leave no outstanding request. The caller owns mount serialization.</summary>
public interface IBlockDevice
{
    int SectorSize { get; }
    ulong SectorCount { get; }
    void Read(ulong firstSector, Span<byte> destination);
    void Write(ulong firstSector, ReadOnlySpan<byte> source);
    void Flush();
    /// <summary>Cooperative scheduling boundary, at most four core I/O requests apart.
    /// May schedule unrelated work; MUST NOT reenter this mount, reuse its scratch,
    /// change media or release mount ownership. Return only to resume this operation.</summary>
    void Cooperate() { }
    bool SupportsDiscard => false;
    uint DiscardAlignmentSectors => 1;
    uint MaximumDiscardSectors => 4096;
    void Discard(ulong firstSector, uint sectorCount) => throw new NotSupportedException();
}

public enum MutationResult
{ Success, InvalidName, NotFound, AlreadyExists, DiskFull, ReserveExhausted, TreeTooDeep, NotDirectory, DirectoryNotEmpty, InvalidOperation }
public readonly record struct FileInfo(ulong Id, ulong Length, ulong ExtentCount, bool IsInline, bool IsDirectory);
public readonly record struct Extent(ulong LogicalUnit, ulong PhysicalUnit, uint UnitCount, bool Unwritten = false);
public readonly record struct CheckReport(ulong Files, ulong FileBytes, ulong DataUnits, ulong FreeUnits, ulong MetadataUnits, int ReservePages);
public struct DirectoryCursor { internal ulong High, Low; internal bool Started; }

internal sealed class AdmissionException(MutationResult result) : Exception
{ internal MutationResult Result { get; } = result; }
