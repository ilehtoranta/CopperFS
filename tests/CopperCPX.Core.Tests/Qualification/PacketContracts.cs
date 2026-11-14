namespace CopperCPX.Qualification;

public enum PacketStatus { ImplementedSubset, Pending, FormatBlocked, NativeLifecycle, UnsupportedByReference }
public sealed record PacketContract(uint Action, string Name, PacketStatus Status, string Coverage);

// Executable inventory, deliberately distinguishes refusal from implementation.
// The existing FFS/SFS dispatcher and SFS2 pointer-based wide ABI are the baseline.
public static class PacketContracts
{
    public static readonly PacketContract[] All =
    [
        new(0,"Startup",PacketStatus.NativeLifecycle,"CpxBootTests"),
        new(5,"Die",PacketStatus.NativeLifecycle,"CpxBootTests"),
        new(7,"CurrentVolume",PacketStatus.ImplementedSubset,"scalar result"),
        new(8,"LocateObject",PacketStatus.ImplementedSubset,"shared lock, invalid lock, missing name"),
        new(9,"RenameDisk",PacketStatus.ImplementedSubset,"journaled UTF-8 label, Latin-1 DOS bytes, 30 characters"),
        new(15,"FreeLock",PacketStatus.ImplementedSubset,"release, null, double release"),
        new(16,"DeleteObject",PacketStatus.ImplementedSubset,"unlink, missing, open-resource refusal"),
        new(17,"RenameObject",PacketStatus.ImplementedSubset,"rename, collision, open-resource refusal"),
        new(18,"MoreCache",PacketStatus.ImplementedSubset,"zero persistent cache; operation scratch is fixed"),
        new(19,"CopyDir",PacketStatus.ImplementedSubset,"shared root lock"),
        new(20,"WaitChar",PacketStatus.UnsupportedByReference,"mounted FFS/SFS return false/209, including on a valid file handle"),
        new(21,"SetProtect",PacketStatus.ImplementedSubset,"protection and refused read/write"),
        new(22,"CreateDir",PacketStatus.ImplementedSubset,"returned lock and resource exhaustion"),
        new(23,"ExamineObject",PacketStatus.ImplementedSubset,"name, comment, date, allocation and size"),
        new(24,"ExamineNext",PacketStatus.ImplementedSubset,"bounded tokens, mutation reseek and aliases"),
        new(25,"DiskInfo",PacketStatus.ImplementedSubset,"allocation-unit volume information"),
        new(26,"Info",PacketStatus.ImplementedSubset,"checked lock and volume information"),
        new(27,"Flush",PacketStatus.ImplementedSubset,"retirement and bounded discard"),
        new(28,"SetComment",PacketStatus.ImplementedSubset,"bounded tag-1 attribute page"),
        new(29,"Parent",PacketStatus.ImplementedSubset,"root, directory, file and hard-link entry parent"),
        new(30,"Timer",PacketStatus.UnsupportedByReference,"mounted FFS/SFS return false/209, record timers use device replies"),
        new(31,"Inhibit",PacketStatus.ImplementedSubset,"barrier, resource refusal, suspended access and fresh remount"),
        new(32,"DiskType",PacketStatus.ImplementedSubset,"CPX4 identity"),
        new(33,"DiskChange",PacketStatus.UnsupportedByReference,"mounted FFS/SFS return false/209, media changes use device events"),
        new(34,"SetDate",PacketStatus.ImplementedSubset,"date input validation"),
        new(40,"SameLock",PacketStatus.ImplementedSubset,"same and different object"),
        new(82,"Read",PacketStatus.ImplementedSubset,"contents, EOF, invalid handle"),
        new(87,"Write",PacketStatus.ImplementedSubset,"committed progress, invalid handle"),
        new(1004,"FindUpdate",PacketStatus.ImplementedSubset,"open/create shared handle"),
        new(1005,"FindInput",PacketStatus.ImplementedSubset,"open existing read-only handle"),
        new(1006,"FindOutput",PacketStatus.ImplementedSubset,"create/truncate exclusive handle"),
        new(1007,"End",PacketStatus.ImplementedSubset,"release and invalid handle"),
        new(1008,"Seek",PacketStatus.ImplementedSubset,"signed narrow position"),
        new(1020,"Format",PacketStatus.ImplementedSubset,"quiescent CPX4 format with persistent label and ordered anchor publication"),
        new(1021,"MakeLink",PacketStatus.ImplementedSubset,"regular-file hard links and bounded Latin-1 soft targets"),
        new(1022,"SetFileSize",PacketStatus.ImplementedSubset,"resize and position clamp"),
        new(1023,"WriteProtect",PacketStatus.ImplementedSubset,"barrier, password key and mutation refusal"),
        new(1024,"ReadLink",PacketStatus.ImplementedSubset,"one-step relative/absolute expansion; 255-byte input path"),
        new(0x43535452,"CopperTrim",PacketStatus.ImplementedSubset,"version 1 capability query, dry run and verified free-space discard"),
        new(26406,"NewReadLink",PacketStatus.ImplementedSubset,"qualified soft and hard link expansion, overlapping buffers and size queries"),
        new(1026,"FhFromLock",PacketStatus.ImplementedSubset,"resource ownership transfer and failure preservation"),
        new(1027,"IsFilesystem",PacketStatus.ImplementedSubset,"DOS true"),
        new(1028,"ChangeMode",PacketStatus.ImplementedSubset,"lock and FileHandle BPTR; mode conflicts"),
        new(1030,"CopyDirFh",PacketStatus.ImplementedSubset,"shared handle duplication and pool exhaustion"),
        new(1031,"ParentFh",PacketStatus.ImplementedSubset,"entry parent identity including hard links"),
        new(1033,"ExamineAll",PacketStatus.ImplementedSubset,"ED_NAME through ED_OWNER, shared bounded cursors; filtered calls use DOS fallback"),
        new(1034,"ExamineFh",PacketStatus.ImplementedSubset,"entry identity and full FIB"),
        new(1035,"ExamineAllEnd",PacketStatus.ImplementedSubset,"owned token retirement without touching the data buffer"),
        new(1036,"SetOwner",PacketStatus.ImplementedSubset,"persistent packed UID/GID, shared attribute page and atomic ownership feature admission"),
        new(2008,"LockRecord",PacketStatus.ImplementedSubset,"cooperative ranges, fixed grants, parked timed waits"),
        new(2009,"FreeRecord",PacketStatus.ImplementedSubset,"exact-range release, wakeup and timer quiescence"),
        new(4097,"AddNotify",PacketStatus.ImplementedSubset,"eight watches, bounded messages and retirement delivery"),
        new(4098,"RemoveNotify",PacketStatus.ImplementedSubset,"request detachment and retained message ownership"),
        new(26400,"Seek64",PacketStatus.ImplementedSubset,"SFS2 paired input/output pointer ABI"),
        new(26401,"SetFileSize64",PacketStatus.ImplementedSubset,"SFS2 paired input/output pointer ABI"),
        new(26402,"LockRecord64",PacketStatus.ImplementedSubset,"SFS2 paired range-pointer ABI and checked overflow"),
        new(26403,"FreeRecord64",PacketStatus.ImplementedSubset,"exact paired range and owner identity"),
        new(26407,"QueryAttribute",PacketStatus.ImplementedSubset,"checked scalar/paired outputs; label query refused"),
        new(26408,"ExamineObject64",PacketStatus.ImplementedSubset,"SFS2 FIB wide offsets"),
        new(26409,"ExamineNext64",PacketStatus.ImplementedSubset,"bounded tokens and SFS2 FIB wide offsets"),
        new(26410,"ExamineFh64",PacketStatus.ImplementedSubset,"entry identity and SFS2 FIB wide offsets")
    ];
}
