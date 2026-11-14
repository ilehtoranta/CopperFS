using System.Runtime.CompilerServices;

namespace CopperFS.Cpx4.Reference;

public enum ScanResult { Pending, Found, End, Invalidated }

/// <summary>Caller-owned scalar allocation search; a Found range is a hint, not a reservation.</summary>
public struct FreeSearch
{
    internal ulong Volume, Generation, Group;
    internal uint Wanted;
    internal int Map;
    internal bool Started, Metadata, NeedGroup, Finished;
    public int PagesRead { get; internal set; }
}
internal struct WalkFrame { internal ulong Address; internal int Slot, Level; }
[InlineArray(8)] internal struct WalkFrames { private WalkFrame first; }
internal struct TreeWalk
{
    internal WalkFrames Path;
    internal ulong Root, Owner, Next, High, Low, Volume, Generation;
    internal TreeKind Kind;
    internal int Depth, Phase, Expected;
    internal bool Reverse, Seeking, Inclusive;
}
[InlineArray(128)] internal struct DirectoryRecord { private byte first; }
[InlineArray(768)] internal struct SearchNameBytes { private byte first; }
public struct NameSearch
{
    internal TreeWalk Walk;
    internal DirectoryRecord PendingEntry;
    internal ulong Alias;
    internal uint InputCrc, Hash;
    internal SearchNameBytes Input;
    internal int InputLength;
    internal bool HasPending, IsAlias, Finished;
    internal ulong FoundId;
    public int PagesRead { get; internal set; }
    public int CandidatesCompared { get; internal set; }
}
public struct AncestrySearch
{
    internal TreeWalk Walk;
    internal ulong Current, Forbidden, Remaining;
    internal bool Finished;
    public int PagesRead { get; internal set; }
}
