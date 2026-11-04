namespace CopperStart.Dos;

internal enum DosNativeExtendedPacketAction : int
{
	Seek64 = 26400,
	SetFileSize64 = 26401,
	LockRecord64 = 26402,
	FreeRecord64 = 26403,
}
